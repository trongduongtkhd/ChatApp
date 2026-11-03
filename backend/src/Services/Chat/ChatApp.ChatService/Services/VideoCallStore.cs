using System.Text.Json;
using System.Text.Json.Serialization;
using ChatApp.ChatService.Dtos;
using Microsoft.AspNetCore.SignalR;
using StackExchange.Redis;

namespace ChatApp.ChatService.Services;

public sealed record VideoCallState
{
    public string Id { get; init; } = "";
    public string GroupId { get; init; } = "";
    public string CallerId { get; init; } = "";
    public string CalleeId { get; init; } = "";
    public string CallerName { get; init; } = "";
    public string CalleeName { get; init; } = "";
    public string CallerConnectionId { get; init; } = "";
    public string CalleeConnectionId { get; init; } = "";
    public string State { get; init; } = "";
    public string Reason { get; init; } = "";
    public long Version { get; init; }
    public long CreatedAt { get; init; }
    public long Deadline { get; init; }
    public long ConnectedAt { get; init; }
    public long CallerVersion { get; init; }
    public long CalleeVersion { get; init; }
    public VideoCallDto ToDto() => new(Id, GroupId, CallerId, CalleeId, CallerName, CalleeName,
        CallerConnectionId, CalleeConnectionId, State, Reason, Version, CreatedAt, Deadline, ConnectedAt);
}

// Một Redis chung cho hai replica. Lua giữ busy hai user và state transition nguyên tử.
// Media/SDP/ICE không lưu ở đây. Deadline dùng Redis TIME để tránh lệch đồng hồ giữa server.
public sealed class VideoCallStore(IConnectionMultiplexer redis)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
        { NumberHandling = JsonNumberHandling.AllowReadingFromString };
    private sealed record Reply(bool Ok, string? Error, VideoCallState? Call, VideoCallState[]? Calls, string[]? Ids);
    private const string Script = """
        local p='video:v1:'
        local op,id,user,conn=ARGV[1],ARGV[2],ARGV[3],ARGV[4]
        local x=cjson.decode(ARGV[5])
        local t=redis.call('TIME'); local now=t[1]*1000+math.floor(t[2]/1000)
        local function key(i) return p..'call:'..i end
        local function read(i)
          local values=redis.call('HGETALL',key(i)); if #values==0 then return nil end
          local c={}; for n=1,#values,2 do c[values[n]]=values[n+1] end
          for _,f in ipairs({'version','createdAt','deadline','connectedAt','callerVersion','calleeVersion'}) do c[f]=tonumber(c[f] or '0') end
          return c
        end
        local function fail(s) return cjson.encode({ok=false,error=s}) end
        local function out(c) return cjson.encode({ok=true,call=c or cjson.null}) end
        local function live(u)
          local k=p..'live:'..u; redis.call('ZREMRANGEBYSCORE',k,'-inf',now)
          return redis.call('ZRANGEBYSCORE',k,'('..now,'+inf')
        end
        local function touch()
          redis.call('ZADD',p..'live:'..user,now+30000,conn); redis.call('EXPIRE',p..'live:'..user,90)
        end
        local function finish(c,reason)
          if c.state=='Ended' then return c end
          redis.call('HSET',key(c.id),'state','Ended','reason',reason)
          redis.call('HINCRBY',key(c.id),'version',1)
          for _,u in ipairs({c.callerId,c.calleeId}) do if redis.call('GET',p..'busy:'..u)==c.id then redis.call('DEL',p..'busy:'..u) end end
          redis.call('ZREM',p..'due',c.id); redis.call('EXPIRE',key(c.id),120)
          return read(c.id)
        end
        local function due(c)
          local k=key(c.id)
          local nextAt=tonumber(redis.call('HGET',k,'lastCaller'))+30000
          if c.state=='Ringing' then nextAt=math.min(nextAt,c.createdAt+30000)
          else
            nextAt=math.min(nextAt,tonumber(redis.call('HGET',k,'lastCallee'))+30000)
            if c.state=='Connecting' then nextAt=math.min(nextAt,tonumber(redis.call('HGET',k,'acceptedAt'))+45000) end
          end
          redis.call('HSET',k,'deadline',nextAt); redis.call('ZADD',p..'due',nextAt,c.id)
          redis.call('EXPIRE',k,300)
          for _,u in ipairs({c.callerId,c.calleeId}) do redis.call('SET',p..'busy:'..u,c.id,'EX',90) end
          return read(c.id)
        end
        if op=='register' then touch(); return out(nil) end
        if op=='live' then local ids=live(user); if #ids==0 then return out(nil) end; return cjson.encode({ok=true,ids=ids}) end
        if op=='active' then local ids=redis.call('ZRANGE',p..'due',0,199); if #ids==0 then return out(nil) end; return cjson.encode({ok=true,ids=ids}) end
        if op=='sweep' then
          local expired=redis.call('ZRANGEBYSCORE',p..'due','-inf',now,'LIMIT',0,100); local result={}
          for _,i in ipairs(expired) do
            local c=read(i)
            if not c then redis.call('ZREM',p..'due',i)
            elseif c.deadline<=now then table.insert(result,finish(c,c.state=='Ringing' and 'no-answer' or 'connection-lost')) end
          end
          if #result==0 then return out(nil) end; return cjson.encode({ok=true,calls=result})
        end
        if op=='disconnect' then
          redis.call('ZREM',p..'live:'..user,conn)
          local i=redis.call('GET',p..'busy:'..user); if not i then return out(nil) end
          local c=read(i); if c and (c.callerConnectionId==conn or c.calleeConnectionId==conn) then return out(finish(c,'connection-lost')) end
          return out(nil)
        end
        if op=='current' then local i=redis.call('GET',p..'busy:'..user); return out(i and read(i) or nil) end
        if op=='start' then
          local old=read(id)
          if old then
            if old.callerId==user and old.callerConnectionId==conn and old.calleeId==x.calleeId then return out(old) end
            return fail('Mã cuộc gọi đã được sử dụng.')
          end
          if redis.call('EXISTS',p..'busy:'..user)==1 then return fail('Bạn đang có một cuộc gọi khác.') end
          if redis.call('EXISTS',p..'busy:'..x.calleeId)==1 then return fail('Người kia đang bận.') end
          if #live(x.calleeId)==0 then return fail('Người kia chưa kết nối. Hãy thử lại khi họ mở ứng dụng.') end
          touch()
          redis.call('HSET',key(id),'id',id,'groupId',x.groupId,'callerId',user,'calleeId',x.calleeId,
            'callerName',x.callerName,'calleeName','','callerConnectionId',conn,'calleeConnectionId','',
            'state','Ringing','reason','','version',1,'createdAt',now,'connectedAt',0,
            'callerVersion',x.callerVersion,'calleeVersion',x.calleeVersion,'lastCaller',now)
          return out(due(read(id)))
        end
        local c=read(id); if not c then return fail('Cuộc gọi không còn hiệu lực.') end
        if op=='read' then return out(c) end
        if op=='force-end' then return out(finish(c,x.reason)) end
        if user~=c.callerId and user~=c.calleeId then return fail('Bạn không thuộc cuộc gọi này.') end
        local caller=user==c.callerId and conn==c.callerConnectionId
        local callee=user==c.calleeId and (c.state=='Ringing' or conn==c.calleeConnectionId)
        if not caller and not callee then return fail('Cuộc gọi đã được xử lý ở phiên khác.') end
        if c.state=='Ended' then return out(c) end
        if c.deadline<=now then return out(finish(c,c.state=='Ringing' and 'no-answer' or 'connection-lost')) end
        if op=='accept' then
          if not callee then return fail('Chỉ người nhận được nhận cuộc gọi.') end
          if c.state~='Ringing' then return out(c) end
          redis.call('HSET',key(id),'state','Connecting','calleeConnectionId',conn,'calleeName',x.name,'acceptedAt',now,'lastCallee',now)
          redis.call('HINCRBY',key(id),'version',1); touch(); return out(due(read(id)))
        end
        if op=='end' then
          if x.reason=='declined' and (not callee or c.state~='Ringing') then return fail('Không thể từ chối cuộc gọi này.') end
          return out(finish(c,x.reason))
        end
        if op=='pulse' then
          touch(); redis.call('HSET',key(id),caller and 'lastCaller' or 'lastCallee',now)
          if x.connected and c.state~='Ringing' then redis.call('HSET',key(id),caller and 'callerConnected' or 'calleeConnected',1) end
          if c.state=='Connecting' and redis.call('HGET',key(id),'callerConnected')=='1' and redis.call('HGET',key(id),'calleeConnected')=='1' then
            redis.call('HSET',key(id),'state','Active','connectedAt',now); redis.call('HINCRBY',key(id),'version',1)
          end
          return out(due(read(id)))
        end
        if op=='signal' then
          if c.state~='Connecting' and c.state~='Active' then return fail('Cuộc gọi chưa được nhận.') end
          if (x.kind=='offer' and not caller) or (x.kind=='answer' and not callee) then return fail('Tín hiệu không đúng vai trò.') end
          local rate=p..'rate:'..id..':'..conn; local n=redis.call('INCR',rate); if n==1 then redis.call('EXPIRE',rate,60) end
          if n>200 then return fail('Quá nhiều tín hiệu kết nối.') end
          return out(c)
        end
        return fail('Thao tác cuộc gọi không hợp lệ.')
        """;

    private async Task<Reply> RunAsync(string op, string id = "", string user = "", string conn = "", object? extra = null)
    {
        var value = await redis.GetDatabase().ScriptEvaluateAsync(Script, [],
            [op, id, user, conn, JsonSerializer.Serialize(extra ?? new { }, Json)]);
        var reply = JsonSerializer.Deserialize<Reply>(value.ToString(), Json)!;
        if (!reply.Ok) throw new HubException(reply.Error ?? "Cuộc gọi chưa xử lý được.");
        return reply;
    }
    public async Task RegisterAsync(string user, string conn) => await RunAsync("register", user: user, conn: conn);
    public async Task<VideoCallState?> DisconnectAsync(string user, string conn) => (await RunAsync("disconnect", user: user, conn: conn)).Call;
    public async Task<string[]> LiveAsync(string user) => (await RunAsync("live", user: user)).Ids ?? [];
    public async Task<string[]> ActiveAsync() => (await RunAsync("active")).Ids ?? [];
    public async Task<VideoCallState[]> SweepAsync() => (await RunAsync("sweep")).Calls ?? [];
    public async Task<VideoCallState?> CurrentAsync(string user) => (await RunAsync("current", user: user)).Call;
    public async Task<VideoCallState?> ReadAsync(string id) => (await RunAsync("read", id)).Call;
    public async Task<VideoCallState> ChangeAsync(string op, string id, string user, string conn, object? extra = null) =>
        (await RunAsync(op, id, user, conn, extra)).Call!;
}
