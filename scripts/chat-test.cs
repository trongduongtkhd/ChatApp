#:package Microsoft.AspNetCore.SignalR.Client@10.0.12
#:property PublishAot=false

// Client test SignalR cho chat-service (Phần 7). File C# chạy thẳng (.NET 10 file-based app), không cần project.
// Mọi request đi qua Gateway (5000) giống Angular: REST /api/..., SignalR /hubs/chat.
//
// Cần chạy trước: docker compose up -d, identity, group, Gateway, chat-service.
// Chạy (tại thư mục gốc):
//   dotnet run scripts/chat-test.cs -- basic   # 2 user chat với nhau, người ngoài bị chặn, không token bị 401
//   dotnet run scripts/chat-test.cs -- grpc    # kiểm tra thành viên (qua gRPC) khi thêm/xóa thành viên, xóa nhóm
//   dotnet run scripts/chat-test.cs -- dup     # gửi trùng messageId (tuần tự + đồng thời) → chỉ một tin
//   dotnet run scripts/chat-test.cs -- load    # bắn 200 tin song song (--count 200 --connections 20) → seq không trùng, không thiếu
//   dotnet run scripts/chat-test.cs -- seqlost # làm hỏng bộ đếm Redis (xóa key, lùi số) → vẫn cấp đúng số
//   dotnet run scripts/chat-test.cs -- cache   # cache thành viên Redis: nạp, TTL, bị xóa khi thêm/xóa thành viên
//   dotnet run scripts/chat-test.cs -- presence # online/offline: nhiều kết nối của một user, đóng lần lượt
//   dotnet run scripts/chat-test.cs -- history # REST lịch sử: lật trang bằng beforeSeq, lỗi 400/401/403/404
//   dotnet run scripts/chat-test.cs -- down    # kịch bản lỗi: tắt group-service giữa chừng (nhóm có cache vẫn chat được) rồi bật lại
// Tùy chọn: --user duong --other lan --outsider minh --password 123456 --gateway http://localhost:5000 --count 200 --connections 20
//
// Lần chạy đầu chậm (~20 giây) vì phải tải package và biên dịch; các lần sau nhanh.

using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;

var opt = Options.Parse(args);
var api = new Api(opt.Gateway);

try
{
    var ok = opt.Mode switch
    {
        "basic" => await Modes.Basic(api, opt),
        "grpc" => await Modes.Grpc(api, opt),
        "dup" => await Modes.Dup(api, opt),
        "load" => await Modes.Load(api, opt),
        "seqlost" => await Modes.SeqLost(api, opt),
        "cache" => await Modes.Cache(api, opt),
        "presence" => await Modes.Presence(api, opt),
        "history" => await Modes.History(api, opt),
        "down" => await Modes.Down(api, opt),
        _ => throw new ArgumentException($"Chế độ không hợp lệ: '{opt.Mode}'. Dùng: basic | grpc | dup | load | seqlost | cache | presence | history | down")
    };
    Out.Result(ok);
    return ok ? 0 : 1;
}
catch (Exception ex)
{
    Out.Fail($"LỖI: {ex.Message}");
    return 2;
}

static class Modes
{
    // Bước 2: A tạo nhóm, thêm B; A và B vào phòng; A gửi 3 tin, B gửi 1 tin → cả hai nhận đủ 4 tin, seq tăng dần.
    // Người ngoài (C) JoinGroup / SendMessage → bị từ chối. Kết nối không token → 401.
    public static async Task<bool> Basic(Api api, Options opt)
    {
        var a = await api.LoginAsync(opt.User, opt.Password);
        var b = await api.LoginAsync(opt.Other, opt.Password);
        var groupId = await api.CreateGroupAsync(a, "Demo chat");
        await api.AddMemberAsync(a, groupId, b.UserId);
        Out.Info($"Nhóm {groupId}: {a.UserName} (Owner) + {b.UserName}");

        await using var connA = await ChatClient.ConnectAsync(opt.Gateway, a);
        await using var connB = await ChatClient.ConnectAsync(opt.Gateway, b);
        await connA.Hub.InvokeAsync("JoinGroup", groupId);
        await connB.Hub.InvokeAsync("JoinGroup", groupId);
        Out.Info($"Đã kết nối hub: {a.UserName} = {connA.Hub.ConnectionId}, {b.UserName} = {connB.Hub.ConnectionId}");

        var sent = new List<MessageDto>();
        foreach (var text in new[] { "Xin chào", "Mình là " + a.UserName, "Test SignalR" })
            sent.Add(await connA.SendAsync(groupId, text));
        sent.Add(await connB.SendAsync(groupId, "Chào " + a.UserName));
        foreach (var m in sent)
            Out.Info($"  gửi xong  seq={m.SequenceNumber,-4} {m.SenderName}: {m.Content}");

        await Task.Delay(500); // chờ ReceiveMessage tới nơi
        var checks = new List<bool>
        {
            Out.Check($"{b.UserName} nhận đủ 4 tin", connB.Received.Count == 4, $"nhận {connB.Received.Count}"),
            Out.Check($"{a.UserName} (người gửi) cũng nhận đủ 4 tin", connA.Received.Count == 4, $"nhận {connA.Received.Count}"),
            Out.Check("seq tăng liên tiếp", IsConsecutive(sent.Select(m => m.SequenceNumber)),
                string.Join(", ", sent.Select(m => m.SequenceNumber)))
        };

        // Người ngoài nhóm: đăng nhập được nhưng không vào phòng, không gửi tin được.
        var c = await api.TryLoginAsync(opt.Outsider, opt.Password);
        if (c is null)
            Out.Warn($"Không đăng nhập được '{opt.Outsider}' → bỏ qua kiểm tra người ngoài (dùng --outsider <user>)");
        else
        {
            await using var connC = await ChatClient.ConnectAsync(opt.Gateway, c);
            checks.Add(await Out.ExpectHubError($"{c.UserName} (ngoài nhóm) JoinGroup bị từ chối",
                () => connC.Hub.InvokeAsync("JoinGroup", groupId)));
            checks.Add(await Out.ExpectHubError($"{c.UserName} (ngoài nhóm) SendMessage bị từ chối",
                () => connC.SendAsync(groupId, "nghe lén")));
            await Task.Delay(300);
            checks.Add(Out.Check($"{b.UserName} không nhận tin của người ngoài", connB.Received.Count == 4, $"nhận {connB.Received.Count}"));
        }

        // Không token: SignalR gọi /hubs/chat/negotiate → Gateway trả 401.
        try
        {
            await using var anon = new HubConnectionBuilder().WithUrl($"{opt.Gateway}/hubs/chat").Build();
            await anon.StartAsync();
            checks.Add(Out.Check("Kết nối không token bị từ chối", false, "lại kết nối được!"));
        }
        catch (HttpRequestException ex)
        {
            checks.Add(Out.Check("Kết nối không token bị từ chối", ex.StatusCode == System.Net.HttpStatusCode.Unauthorized, $"{(int?)ex.StatusCode}"));
        }

        Out.Info($"\nXem trong DB: docker exec postgres psql -U chatapp -d chat_db -c \"select sequence_number, sender_name, content from messages where group_id = '{groupId}' order by sequence_number\"");
        Out.Info($"Xem bộ đếm:  docker exec redis redis-cli GET chat:seq:{groupId}");
        return checks.All(x => x);
    }

    // Thay cho scripts/test-grpc.ps1 bản Phần 6 (endpoint /debug/membership đã xóa).
    // Từ Bước 5 hub hỏi cache Redis trước, cache trống mới gọi gRPC → thêm/xóa thành viên có hiệu lực sau ~1–2 giây
    // (chờ Kafka xóa cache), không còn "ngay lập tức" như khi gọi gRPC mỗi lần. Script đo khoảng trễ đó.
    public static async Task<bool> Grpc(Api api, Options opt)
    {
        var a = await api.LoginAsync(opt.User, opt.Password);
        var b = await api.LoginAsync(opt.Other, opt.Password);
        var groupId = await api.CreateGroupAsync(a, "Demo gRPC");
        Out.Info($"Nhóm {groupId}: Owner = {a.UserName}, người thứ hai = {b.UserName}");

        await using var connA = await ChatClient.ConnectAsync(opt.Gateway, a);
        await using var connB = await ChatClient.ConnectAsync(opt.Gateway, b);

        var checks = new List<bool>
        {
            await Out.ExpectOk($"Owner ({a.UserName}) JoinGroup", () => connA.Hub.InvokeAsync("JoinGroup", groupId)),
            await Out.ExpectHubError($"{b.UserName}, chưa được thêm → JoinGroup bị từ chối", () => connB.Hub.InvokeAsync("JoinGroup", groupId))
        };

        await api.AddMemberAsync(a, groupId, b.UserId);
        checks.Add(await Out.Eventually($"{b.UserName}, vừa được thêm → JoinGroup được", expectOk: true,
            () => connB.Hub.InvokeAsync("JoinGroup", groupId)));

        await api.DeleteGroupAsync(a, groupId);
        checks.Add(await Out.Eventually($"Owner, sau khi xóa nhóm → JoinGroup bị từ chối", expectOk: false,
            () => connA.Hub.InvokeAsync("JoinGroup", groupId)));

        Out.Info("\nDemo lỗi (group-service chết giữa chừng): dotnet run scripts/chat-test.cs -- down");
        return checks.All(x => x);
    }

    // Bước 3 – idempotency: gửi CÙNG messageId nhiều lần (tuần tự, rồi đồng thời từ 5 kết nối) → chỉ MỘT tin.
    public static async Task<bool> Dup(Api api, Options opt)
    {
        var a = await api.LoginAsync(opt.User, opt.Password);
        var b = await api.LoginAsync(opt.Other, opt.Password);
        var groupId = await api.CreateGroupAsync(a, "Demo gửi trùng");
        await api.AddMemberAsync(a, groupId, b.UserId);
        Out.Info($"Nhóm {groupId}: {a.UserName} (Owner) + {b.UserName}\n");

        await using var connA = await ChatClient.ConnectAsync(opt.Gateway, a);
        await using var connB = await ChatClient.ConnectAsync(opt.Gateway, b);
        await connB.Hub.InvokeAsync("JoinGroup", groupId);
        var checks = new List<bool>();

        // 1. Tuần tự: giống client không nhận được trả lời nên gửi lại.
        var id1 = Guid.CreateVersion7();
        var first = await connA.SendAsync(groupId, "Tin gửi 2 lần (tuần tự)", id1);
        var again = await connA.SendAsync(groupId, "Tin gửi 2 lần (tuần tự)", id1);
        Out.Info($"Tuần tự  messageId {id1}: lần 1 seq={first.SequenceNumber}, lần 2 seq={again.SequenceNumber}");
        checks.Add(Out.Check("Lần 2 trả về đúng tin cũ (cùng seq)", first.SequenceNumber == again.SequenceNumber));

        // 2. Đồng thời: 5 kết nối của cùng user gửi cùng messageId một lúc.
        //    (Một kết nối SignalR xử lý lần lượt từng lời gọi, nên phải dùng nhiều kết nối mới thật sự song song.)
        var id2 = Guid.CreateVersion7();
        var conns = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => ChatClient.ConnectAsync(opt.Gateway, a)));
        var results = await Task.WhenAll(conns.Select(c => c.SendAsync(groupId, "Tin gửi 5 lần (đồng thời)", id2)));
        foreach (var c in conns) await c.DisposeAsync();
        Out.Info($"Đồng thời messageId {id2}: seq trả về = {string.Join(", ", results.Select(r => r.SequenceNumber))}");
        checks.Add(Out.Check("Cả 5 lần trả về cùng một tin", results.Select(r => r.SequenceNumber).Distinct().Count() == 1));

        // 3. Người khác dùng lại messageId của tin đã có → bị từ chối, không lộ nội dung.
        checks.Add(await Out.ExpectHubError($"{b.UserName} dùng lại messageId của {a.UserName} → bị từ chối",
            () => connB.SendAsync(groupId, "đoán id", id1)));

        // 4. Một tin bình thường để thấy số tiếp theo (có thể "nhảy" nếu ở bước 2 có lần INCR bị bỏ phí).
        var next = await connA.SendAsync(groupId, "Tin bình thường sau cùng");
        Out.Info($"Tin bình thường tiếp theo: seq={next.SequenceNumber}");

        await Task.Delay(500);
        checks.Add(Out.Check($"{b.UserName} chỉ nhận 3 ReceiveMessage (không nhận bản trùng)", connB.Received.Count == 3,
            $"nhận {connB.Received.Count}: seq {string.Join(", ", connB.Received.Select(m => m.SequenceNumber))}"));

        Out.Info($"\nDB phải có đúng 3 tin và 3 dòng outbox:");
        Out.Info($"  docker exec postgres psql -U chatapp -d chat_db -c \"select sequence_number, id, content from messages where group_id = '{groupId}' order by sequence_number\"");
        Out.Info($"  docker exec postgres psql -U chatapp -d chat_db -c \"select event_type, key, processed_at, payload->>'sequenceNumber' as seq from outbox_messages where key = '{groupId}' order by occurred_at\"");
        return checks.All(x => x);
    }

    // Bước 4 – bắn N tin song song (mặc định 200 tin, 20 kết nối, 2 user) → seq không trùng, không thiếu.
    public static async Task<bool> Load(Api api, Options opt)
    {
        var a = await api.LoginAsync(opt.User, opt.Password);
        var b = await api.LoginAsync(opt.Other, opt.Password);
        var groupId = await api.CreateGroupAsync(a, "Demo 200 tin");
        await api.AddMemberAsync(a, groupId, b.UserId);
        Out.Info($"Nhóm {groupId}: bắn {opt.Count} tin qua {opt.Connections} kết nối ({a.UserName} + {b.UserName})");

        // Người nghe: một kết nối của B ngồi trong phòng, đếm ReceiveMessage.
        await using var listener = await ChatClient.ConnectAsync(opt.Gateway, b);
        await listener.Hub.InvokeAsync("JoinGroup", groupId);

        // Một nửa kết nối của A, một nửa của B. Mỗi kết nối gửi lần lượt phần của mình; các kết nối chạy song song.
        var conns = await Task.WhenAll(Enumerable.Range(0, opt.Connections)
            .Select(i => ChatClient.ConnectAsync(opt.Gateway, i % 2 == 0 ? a : b)));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var perConn = await Task.WhenAll(conns.Select(async (c, i) =>
        {
            var mine = new List<MessageDto>();
            for (var k = i; k < opt.Count; k += opt.Connections)
                mine.Add(await c.SendAsync(groupId, $"tin #{k}"));
            return mine;
        }));
        sw.Stop();
        foreach (var c in conns) await c.DisposeAsync();

        var sent = perConn.SelectMany(x => x).ToList();
        var seqs = sent.Select(m => m.SequenceNumber).OrderBy(s => s).ToList();
        Out.Info($"Gửi xong {sent.Count} tin trong {sw.ElapsedMilliseconds} ms (~{sent.Count * 1000 / Math.Max(1, sw.ElapsedMilliseconds)} tin/giây)");
        Out.Info($"seq nhỏ nhất = {seqs.First()}, lớn nhất = {seqs.Last()}");

        await Task.Delay(1000);
        List<MessageDto> received;
        lock (listener.Received) received = [.. listener.Received];
        // Tin đến người nghe theo thứ tự PHÁT, không nhất thiết theo seq: tin seq 7 có thể lưu xong trước tin seq 6.
        var outOfOrder = received.Zip(received.Skip(1)).Count(p => p.Second.SequenceNumber < p.First.SequenceNumber);

        var checks = new List<bool>
        {
            Out.Check($"Cả {opt.Count} lời gọi thành công", sent.Count == opt.Count, $"{sent.Count}"),
            Out.Check("Không có 2 tin trùng seq", seqs.Distinct().Count() == seqs.Count),
            Out.Check("Không thiếu số nào (liên tục 1..N)", seqs.First() == 1 && seqs.Last() == opt.Count),
            Out.Check($"Người nghe nhận đủ {opt.Count} tin", received.Count == opt.Count, $"{received.Count}")
        };
        Out.Info($"Số lần tin đến người nghe KHÔNG theo thứ tự seq: {outOfOrder} → client phải tự sắp theo sequenceNumber");

        Out.Info($"\nKiểm tra trong DB:");
        Out.Info($"  docker exec postgres psql -U chatapp -d chat_db -c \"select count(*), count(distinct sequence_number), min(sequence_number), max(sequence_number) from messages where group_id = '{groupId}'\"");
        return checks.All(x => x);
    }

    // Bước 4 – bộ đếm Redis hỏng: (1) mất key, (2) key bị lùi về số cũ, (3) mất key đúng lúc nhiều người đang gửi.
    // Script tự làm hỏng Redis bằng "docker exec redis redis-cli ...".
    public static async Task<bool> SeqLost(Api api, Options opt)
    {
        var a = await api.LoginAsync(opt.User, opt.Password);
        var groupId = await api.CreateGroupAsync(a, "Demo mất bộ đếm");
        var key = $"chat:seq:{groupId}";
        await using var conn = await ChatClient.ConnectAsync(opt.Gateway, a);
        for (var i = 1; i <= 3; i++) await conn.SendAsync(groupId, $"tin {i}");
        Out.Info($"Nhóm {groupId}: đã gửi 3 tin, {key} = {Redis($"GET {key}")}\n");
        var checks = new List<bool>();

        // (1) Mất key: không có EXISTS + khởi tạo từ MAX thì INCR sẽ trả 1 → trùng.
        Out.Info($"(1) redis-cli DEL {key} → {Redis($"DEL {key}")}");
        var m4 = await conn.SendAsync(groupId, "sau khi mất key");
        checks.Add(Out.Check("Tin tiếp theo có seq = 4 (khởi tạo lại từ MAX trong DB)", m4.SequenceNumber == 4, $"seq={m4.SequenceNumber}"));

        // (2) Key bị lùi (giống Redis khôi phục từ bản sao lưu cũ): EXISTS = có nên không phát hiện trước được.
        //     INCR → 2 (đã dùng) → unique index từ chối → đồng bộ lại → thử lại.
        Out.Info($"(2) redis-cli SET {key} 1 → {Redis($"SET {key} 1")}");
        var m5 = await conn.SendAsync(groupId, "sau khi bộ đếm bị lùi");
        checks.Add(Out.Check("Tin tiếp theo có seq = 5 (phát hiện trùng, đồng bộ lại, thử lại)", m5.SequenceNumber == 5, $"seq={m5.SequenceNumber}"));

        // (3) Mất key trong lúc 20 kết nối đang gửi cùng lúc (400 tin, đủ lâu để lệnh DEL rơi vào giữa chừng;
        //     "docker exec" mất vài trăm ms mới chạy).
        const int total = 400;
        var conns = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => ChatClient.ConnectAsync(opt.Gateway, a)));
        var sending = Task.WhenAll(conns.Select(async c =>
        {
            var mine = new List<MessageDto>();
            for (var k = 0; k < total / 20; k++) mine.Add(await c.SendAsync(groupId, "song song"));
            return mine;
        }));
        await Task.Delay(100);
        Out.Info($"(3) Đang có 20 kết nối gửi {total} tin, redis-cli DEL {key} → {Redis($"DEL {key}")}");
        var parallel = (await sending).SelectMany(x => x).Select(m => m.SequenceNumber).ToList();
        foreach (var c in conns) await c.DisposeAsync();
        checks.Add(Out.Check($"{total} tin song song đều lưu được, không trùng seq",
            parallel.Count == total && parallel.Distinct().Count() == total, $"{parallel.Count} tin, {parallel.Distinct().Count()} seq khác nhau"));

        Out.Info($"\nXem log chat-service: các dòng \"Đồng bộ lại bộ đếm {key} ...\"");
        Out.Info($"  docker exec postgres psql -U chatapp -d chat_db -c \"select count(*), count(distinct sequence_number), max(sequence_number) from messages where group_id = '{groupId}'\"");
        return checks.All(x => x);
    }

    static string Redis(string command)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("docker", $"exec redis redis-cli {command}") { RedirectStandardOutput = true };
        using var p = System.Diagnostics.Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd().Trim();
        p.WaitForExit();
        return output;
    }

    // Kịch bản lỗi: group-service chết giữa chừng.
    // - Nhóm ĐÃ có trong cache Redis → vẫn chat được (không cần hỏi group-service).
    // - Nhóm CHƯA có trong cache → phải hỏi gRPC → không kiểm tra được → TỪ CHỐI (không cho qua bừa).
    // Bật lại group-service → nhóm chưa cache cũng gửi được ngay, không cần khởi động lại chat-service hay kết nối lại hub.
    public static async Task<bool> Down(Api api, Options opt)
    {
        var a = await api.LoginAsync(opt.User, opt.Password);
        var cachedGroup = await api.CreateGroupAsync(a, "Demo group-service chết (có cache)");
        var uncachedGroup = await api.CreateGroupAsync(a, "Demo group-service chết (chưa cache)");
        await using var connA = await ChatClient.ConnectAsync(opt.Gateway, a);

        // Tạo nhóm → group-service phát member-added (Owner) qua outbox, tới chat-service sau ~1 giây và XÓA cache.
        // Nạp cache ngay lúc này thì sự kiện đến muộn sẽ xóa mất → chờ sự kiện xử lý xong rồi mới nạp.
        await Task.Delay(3000);
        var checks = new List<bool>
        {
            // JoinGroup → cache miss → gọi gRPC → nạp cache group:members:{cachedGroup}.
            await Out.ExpectOk("group-service đang chạy → JoinGroup nhóm 1 (nạp cache)", () => connA.Hub.InvokeAsync("JoinGroup", cachedGroup)),
            Out.Check("Nhóm 1 đã có cache", Redis($"EXISTS group:members:{cachedGroup}") == "1")
        };
        Out.Info($"  cache: redis-cli SMEMBERS group:members:{cachedGroup} → {Redis($"SMEMBERS group:members:{cachedGroup}")}");
        Out.Info($"  nhóm 2 chưa ai hỏi: redis-cli EXISTS group:members:{uncachedGroup} → {Redis($"EXISTS group:members:{uncachedGroup}")}");

        Out.Warn("\n>>> Hãy TẮT group-service (Ctrl+C ở cửa sổ của nó). Script tự phát hiện...");
        await api.WaitGroupServiceAsync(a, up: false);
        Out.Info("Đã phát hiện group-service tắt (Gateway trả 502).");

        var sw = System.Diagnostics.Stopwatch.StartNew();
        checks.Add(await Out.ExpectOk("Nhóm 1 (có cache) → SendMessage VẪN được", () => connA.SendAsync(cachedGroup, "lúc group-service chết")));
        Out.Info($"  mất {sw.ElapsedMilliseconds} ms (không gọi gRPC)");

        sw.Restart();
        checks.Add(await Out.ExpectHubError("Nhóm 2 (chưa cache) → SendMessage bị từ chối", () => connA.SendAsync(uncachedGroup, "lúc group-service chết")));
        // DeadlineExceeded (~3 s, đang chờ kết nối) hoặc Unavailable (nhanh hơn: gRPC client đã biết kết nối hỏng
        // và đang trong thời gian chờ "reconnect backoff" → từ chối ngay, không chờ hết deadline).
        Out.Info($"  mất {sw.ElapsedMilliseconds} ms");
        checks.Add(Out.Check("Kết nối hub vẫn còn", connA.Hub.State == HubConnectionState.Connected, connA.Hub.State.ToString()));

        Out.Warn("\n>>> Hãy BẬT LẠI group-service. Script tự phát hiện...");
        await api.WaitGroupServiceAsync(a, up: true);
        // gRPC client thử kết nối lại theo backoff tăng dần (1 s, 1.6 s, 2.5 s...) → có thể mất vài giây mới thông.
        checks.Add(await Out.Eventually("group-service sống lại → nhóm 2 SendMessage được, cùng kết nối hub cũ", expectOk: true,
            () => connA.SendAsync(uncachedGroup, "group-service đã sống lại"), timeoutMs: 30000));
        return checks.All(x => x);
    }

    // Bước 5 – cache thành viên: nạp khi cần, có TTL, bị xóa khi thêm/xóa thành viên (qua Kafka).
    public static async Task<bool> Cache(Api api, Options opt)
    {
        var a = await api.LoginAsync(opt.User, opt.Password);
        var b = await api.LoginAsync(opt.Other, opt.Password);
        var groupId = await api.CreateGroupAsync(a, "Demo cache thành viên");
        var key = $"group:members:{groupId}";
        await using var connA = await ChatClient.ConnectAsync(opt.Gateway, a);
        await using var connB = await ChatClient.ConnectAsync(opt.Gateway, b);
        Out.Info($"Nhóm {groupId}: Owner = {a.UserName} ({a.UserId}), {b.UserName} = {b.UserId}\n");
        var checks = new List<bool>();

        // 1. Chưa ai hỏi → chưa có cache. Gửi tin → cache miss → gRPC → nạp cache.
        checks.Add(Out.Check("Ban đầu chưa có cache", Redis($"EXISTS {key}") == "0"));
        await connA.SendAsync(groupId, "tin đầu tiên");
        var members = Redis($"SMEMBERS {key}");
        var ttl = int.Parse(Redis($"TTL {key}"));
        Out.Info($"Sau khi gửi tin: SMEMBERS = {members.Replace('\n', ' ')}, TTL = {ttl} giây");
        checks.Add(Out.Check("Cache có Owner, TTL ~600 giây", members.Contains(a.UserId.ToString()) && ttl is > 500 and <= 600));

        // 2. Thêm B qua REST → group-service phát member-added → chat-service xóa cache → B gửi được.
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await api.AddMemberAsync(a, groupId, b.UserId);
        var gone = await WaitUntil(() => Redis($"EXISTS {key}") == "0");
        checks.Add(Out.Check("Thêm thành viên → cache bị xóa", gone, $"sau {sw.ElapsedMilliseconds} ms"));
        checks.Add(await Out.ExpectOk($"{b.UserName} gửi tin được", () => connB.SendAsync(groupId, "mình mới vào")));
        checks.Add(Out.Check($"Cache nạp lại có cả {b.UserName}", Redis($"SMEMBERS {key}").Contains(b.UserId.ToString())));

        // 3. Xóa B → member-removed → cache bị xóa → B không gửi được nữa.
        sw.Restart();
        await api.RemoveMemberAsync(a, groupId, b.UserId);
        gone = await WaitUntil(() => Redis($"EXISTS {key}") == "0");
        checks.Add(Out.Check("Xóa thành viên → cache bị xóa", gone, $"sau {sw.ElapsedMilliseconds} ms"));
        checks.Add(await Out.ExpectHubError($"{b.UserName} (đã bị xóa) gửi tin → bị từ chối", () => connB.SendAsync(groupId, "còn gửi được không?")));

        Out.Info($"\nXem log chat-service: \"Nạp cache {key} từ gRPC\" và \"Xóa cache thành viên {key}\".");
        return checks.All(x => x);
    }

    // Bước 6 – presence: A ngồi trong phòng; B mở 2 kết nối, đóng lần lượt.
    // Đóng kết nối thứ nhất → A KHÔNG nhận offline (B vẫn còn tab khác). Đóng kết nối cuối → A nhận offline.
    public static async Task<bool> Presence(Api api, Options opt)
    {
        var a = await api.LoginAsync(opt.User, opt.Password);
        var b = await api.LoginAsync(opt.Other, opt.Password);
        var groupId = await api.CreateGroupAsync(a, "Demo presence");
        await api.AddMemberAsync(a, groupId, b.UserId);
        var keyB = $"presence:{b.UserId}";
        Out.Info($"Nhóm {groupId}: {a.UserName} + {b.UserName} ({b.UserId})\n");
        var checks = new List<bool>();

        await using var connA = await ChatClient.ConnectAsync(opt.Gateway, a);
        var onlineSeenByA = await connA.Hub.InvokeAsync<List<Guid>>("JoinGroup", groupId);
        Out.Info($"{a.UserName} JoinGroup → online: {Names(onlineSeenByA, a, b)}");
        checks.Add(Out.Check($"Lúc đầu chỉ {a.UserName} online", onlineSeenByA.Count == 1 && onlineSeenByA[0] == a.UserId));

        // B mở kết nối 1 → vào phòng → A nhận "B online"; JoinGroup của B trả về cả A và B.
        var b1 = await ChatClient.ConnectAsync(opt.Gateway, b);
        var onlineSeenByB = await b1.Hub.InvokeAsync<List<Guid>>("JoinGroup", groupId);
        Out.Info($"{b.UserName} (kết nối 1) JoinGroup → online: {Names(onlineSeenByB, a, b)}");
        checks.Add(Out.Check($"{b.UserName} thấy cả 2 online", onlineSeenByB.Count == 2));
        checks.Add(Out.Check($"{a.UserName} nhận UserPresenceChanged({b.UserName}, true)",
            await WaitUntil(() => HasEvent(connA, b.UserId, true), 3000)));

        // B mở kết nối 2 (tab thứ hai).
        var b2 = await ChatClient.ConnectAsync(opt.Gateway, b);
        await b2.Hub.InvokeAsync<List<Guid>>("JoinGroup", groupId);
        Out.Info($"{b.UserName} mở kết nối 2 → redis-cli SCARD {keyB} = {Redis($"SCARD {keyB}")}");
        checks.Add(Out.Check($"{keyB} có 2 ConnectionId", Redis($"SCARD {keyB}") == "2"));

        // Đóng kết nối 1 → vẫn còn kết nối 2 → KHÔNG báo offline.
        await b1.DisposeAsync();
        await Task.Delay(1000);
        checks.Add(Out.Check($"Đóng kết nối 1 → {a.UserName} KHÔNG nhận offline", !HasEvent(connA, b.UserId, false)));
        checks.Add(Out.Check($"{keyB} còn 1 ConnectionId", Redis($"SCARD {keyB}") == "1"));

        // Đóng kết nối cuối → offline.
        await b2.DisposeAsync();
        checks.Add(Out.Check($"Đóng kết nối cuối → {a.UserName} nhận UserPresenceChanged({b.UserName}, false)",
            await WaitUntil(() => HasEvent(connA, b.UserId, false), 3000)));
        checks.Add(Out.Check($"{keyB} bị xóa (set rỗng)", Redis($"EXISTS {keyB}") == "0"));

        var after = await connA.Hub.InvokeAsync<List<Guid>>("JoinGroup", groupId);
        checks.Add(Out.Check($"{a.UserName} JoinGroup lại → chỉ còn {a.UserName} online", after.Count == 1 && after[0] == a.UserId,
            Names(after, a, b)));

        Out.Info($"\nDemo lỗi (kết nối \"ma\"): giữ một kết nối của {b.UserName}, giết tiến trình chat-service, rồi:");
        Out.Info($"  docker exec redis redis-cli SMEMBERS {keyB}   → ConnectionId cũ vẫn còn, {b.UserName} bị coi là online mãi");
        return checks.All(x => x);

        static bool HasEvent(ChatClient c, Guid userId, bool online)
        {
            lock (c.Presence) return c.Presence.Contains((userId, online));
        }
        static string Names(List<Guid> ids, LoggedInUser a, LoggedInUser b) =>
            string.Join(", ", ids.Select(id => id == a.UserId ? a.UserName : id == b.UserId ? b.UserName : id.ToString()));
    }

    // Bước 7 – REST lịch sử: 120 tin, lật trang 50 → 50 → 20 → rỗng bằng beforeSeq.
    // Giữa trang 1 và 2 có 5 tin MỚI chen vào → trang sau vẫn đúng chỗ (keyset), không lặp, không sót.
    public static async Task<bool> History(Api api, Options opt)
    {
        var a = await api.LoginAsync(opt.User, opt.Password);
        var groupId = await api.CreateGroupAsync(a, "Demo lịch sử");
        var url = $"/api/chat/groups/{groupId}/messages";
        await using var conn = await ChatClient.ConnectAsync(opt.Gateway, a);
        for (var i = 1; i <= 120; i++) await conn.SendAsync(groupId, $"tin {i}");
        Out.Info($"Nhóm {groupId}: đã gửi 120 tin (seq 1..120)\n");
        var checks = new List<bool>();

        var page1 = await api.GetMessagesAsync(a, url);
        Out.Info($"GET {url}                → {Range(page1)}");
        checks.Add(Out.Check("Trang 1 = 50 tin mới nhất, cũ → mới", page1.Count == 50 && page1[0].SequenceNumber == 71 && page1[^1].SequenceNumber == 120));

        for (var i = 1; i <= 5; i++) await conn.SendAsync(groupId, $"tin mới chen vào {i}");
        Out.Info("(5 tin mới chen vào: seq 121..125)");

        var page2 = await api.GetMessagesAsync(a, $"{url}?beforeSeq={page1[0].SequenceNumber}");
        Out.Info($"GET ...?beforeSeq={page1[0].SequenceNumber,-4}         → {Range(page2)}");
        var page3 = await api.GetMessagesAsync(a, $"{url}?beforeSeq={page2[0].SequenceNumber}");
        Out.Info($"GET ...?beforeSeq={page2[0].SequenceNumber,-4}         → {Range(page3)}");
        var page4 = await api.GetMessagesAsync(a, $"{url}?beforeSeq={page3[0].SequenceNumber}");
        Out.Info($"GET ...?beforeSeq={page3[0].SequenceNumber,-4}         → {Range(page4)}");

        var all = page3.Concat(page2).Concat(page1).Select(m => m.SequenceNumber).ToList();
        checks.Add(Out.Check("Trang 2 = 21..70 dù có tin mới chen vào", page2.Count == 50 && page2[0].SequenceNumber == 21 && page2[^1].SequenceNumber == 70));
        checks.Add(Out.Check("Trang 3 = 20 tin cuối (1..20), trang 4 rỗng = hết", page3.Count == 20 && page4.Count == 0));
        checks.Add(Out.Check("Ghép 3 trang = 1..120, không lặp, không sót", all.SequenceEqual(Enumerable.Range(1, 120).Select(x => (long)x))));

        var small = await api.GetMessagesAsync(a, $"{url}?limit=3");
        checks.Add(Out.Check("limit=3 → 3 tin mới nhất (123..125)", small.Select(m => m.SequenceNumber).SequenceEqual([123L, 124L, 125L])));

        // Lỗi
        checks.Add(Out.Check("limit=0 → 400", await api.GetStatusAsync(a, $"{url}?limit=0") == 400));
        checks.Add(Out.Check("limit=101 → 400", await api.GetStatusAsync(a, $"{url}?limit=101") == 400));
        checks.Add(Out.Check("groupId không phải GUID → 404", await api.GetStatusAsync(a, "/api/chat/groups/abc/messages") == 404));
        checks.Add(Out.Check("Không token → 401", await api.GetStatusAsync(null, url) == 401));
        var c = await api.TryLoginAsync(opt.Outsider, opt.Password);
        if (c is not null)
            checks.Add(Out.Check($"{c.UserName} (ngoài nhóm) → 403", await api.GetStatusAsync(c, url) == 403));

        Out.Info($"\nXem kế hoạch truy vấn (dùng index, không quét cả bảng):");
        Out.Info($"  docker exec postgres psql -U chatapp -d chat_db -c \"explain analyze select * from messages where group_id = '{groupId}' and sequence_number < 71 order by sequence_number desc limit 50\"");
        return checks.All(x => x);

        static string Range(List<MessageDto> page) =>
            page.Count == 0 ? "[] (hết tin)" : $"{page.Count} tin, seq {page[0].SequenceNumber}..{page[^1].SequenceNumber}";
    }

    static async Task<bool> WaitUntil(Func<bool> condition, int timeoutMs = 10000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (condition()) return true;
            await Task.Delay(200);
        }
        return false;
    }

    static bool IsConsecutive(IEnumerable<long> seqs)
    {
        var list = seqs.ToList();
        return list.Zip(list.Skip(1)).All(p => p.Second == p.First + 1);
    }
}

// ---------------- Hạ tầng của script ----------------

record MessageDto(Guid Id, Guid GroupId, Guid SenderId, string SenderName, string Content, long SequenceNumber, DateTimeOffset CreatedAt);

record LoggedInUser(string UserName, Guid UserId, string Token);

// Một kết nối SignalR của một user, ghi lại mọi ReceiveMessage nhận được.
sealed class ChatClient : IAsyncDisposable
{
    public HubConnection Hub { get; }
    public List<MessageDto> Received { get; } = [];
    public List<(Guid UserId, bool IsOnline)> Presence { get; } = [];

    ChatClient(HubConnection hub) => Hub = hub;

    public static async Task<ChatClient> ConnectAsync(string gateway, LoggedInUser user)
    {
        // AccessTokenProvider: thư viện tự gắn token (header cho negotiate, ?access_token= cho WebSocket).
        var hub = new HubConnectionBuilder()
            .WithUrl($"{gateway}/hubs/chat", o => o.AccessTokenProvider = () => Task.FromResult<string?>(user.Token))
            .Build();
        var client = new ChatClient(hub);
        hub.On<MessageDto>("ReceiveMessage", m => { lock (client.Received) client.Received.Add(m); });
        hub.On<Guid, bool>("UserPresenceChanged", (u, online) => { lock (client.Presence) client.Presence.Add((u, online)); });
        await hub.StartAsync();
        return client;
    }

    public Task<MessageDto> SendAsync(Guid groupId, string content, Guid? messageId = null) =>
        Hub.InvokeAsync<MessageDto>("SendMessage", messageId ?? Guid.CreateVersion7(), groupId, content);

    public ValueTask DisposeAsync() => Hub.DisposeAsync();
}

sealed class Api(string gateway)
{
    readonly HttpClient _http = new() { BaseAddress = new Uri(gateway) };

    public async Task<LoggedInUser> LoginAsync(string userName, string password) =>
        await TryLoginAsync(userName, password)
        ?? throw new InvalidOperationException($"Đăng nhập '{userName}' thất bại (sai mật khẩu hoặc chưa đăng ký; dùng --user/--other/--password)");

    public async Task<LoggedInUser?> TryLoginAsync(string userName, string password)
    {
        var resp = await _http.PostAsJsonAsync("/api/auth/login", new { userName, password });
        if (!resp.IsSuccessStatusCode) return null;
        var token = (await resp.Content.ReadFromJsonAsync<LoginResponse>())!.AccessToken;
        var me = await Send<UserDto>(HttpMethod.Get, "/api/users/me", token);
        return new LoggedInUser(userName, me.Id, token);
    }

    public async Task<Guid> CreateGroupAsync(LoggedInUser owner, string name) =>
        (await Send<IdDto>(HttpMethod.Post, "/api/groups", owner.Token, new { name })).Id;

    public async Task AddMemberAsync(LoggedInUser owner, Guid groupId, Guid userId)
    {
        var resp = await SendRaw(HttpMethod.Post, $"/api/groups/{groupId}/members", owner.Token, new { userId });
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
            throw new InvalidOperationException($"Thêm thành viên {userId} → 404: user chưa có trong user_snapshots (đăng ký lại user sau Phần 5)");
        resp.EnsureSuccessStatusCode();
    }

    public Task<List<MessageDto>> GetMessagesAsync(LoggedInUser user, string path) =>
        Send<List<MessageDto>>(HttpMethod.Get, path, user.Token);

    // user = null → gửi không token.
    public async Task<int> GetStatusAsync(LoggedInUser? user, string path)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, path);
        if (user is not null) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", user.Token);
        return (int)(await _http.SendAsync(req)).StatusCode;
    }

    public async Task RemoveMemberAsync(LoggedInUser owner, Guid groupId, Guid userId) =>
        (await SendRaw(HttpMethod.Delete, $"/api/groups/{groupId}/members/{userId}", owner.Token)).EnsureSuccessStatusCode();

    public async Task DeleteGroupAsync(LoggedInUser owner, Guid groupId) =>
        (await SendRaw(HttpMethod.Delete, $"/api/groups/{groupId}", owner.Token)).EnsureSuccessStatusCode();

    // Hỏi Gateway mỗi giây tới khi group-service ở trạng thái mong muốn. 502 Bad Gateway = service phía sau không trả lời.
    // Phải có token thật: không token thì Gateway chặn 401 trước, không bao giờ tới group-service.
    public async Task WaitGroupServiceAsync(LoggedInUser user, bool up)
    {
        while (true)
        {
            var resp = await SendRaw(HttpMethod.Get, "/api/groups", user.Token);
            var isUp = resp.StatusCode != System.Net.HttpStatusCode.BadGateway;
            if (isUp == up) return;
            await Task.Delay(1000);
        }
    }

    async Task<T> Send<T>(HttpMethod method, string path, string token, object? body = null)
    {
        var resp = await SendRaw(method, path, token, body);
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<T>())!;
    }

    Task<HttpResponseMessage> SendRaw(HttpMethod method, string path, string token, object? body = null)
    {
        var req = new HttpRequestMessage(method, path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null) req.Content = JsonContent.Create(body);
        return _http.SendAsync(req);
    }

    record LoginResponse(string AccessToken);
    record UserDto(Guid Id);
    record IdDto(Guid Id);
}

sealed record Options(string Mode, string User, string Other, string Outsider, string Password, string Gateway, int Count, int Connections)
{
    public static Options Parse(string[] args)
    {
        string Get(string name, string fallback)
        {
            var i = Array.IndexOf(args, "--" + name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback;
        }
        // Tham số đầu tiên (nếu không bắt đầu bằng --) là chế độ.
        var mode = args.Length > 0 && !args[0].StartsWith("--") ? args[0] : "basic";
        return new Options(mode, Get("user", "duong"), Get("other", "lan"), Get("outsider", "minh"),
            Get("password", "123456"), Get("gateway", "http://localhost:5000"),
            int.Parse(Get("count", "200")), int.Parse(Get("connections", "20")));
    }
}

static class Out
{
    public static void Info(string s) => Console.WriteLine(s);
    public static void Warn(string s) => Write(ConsoleColor.Yellow, s);
    public static void Fail(string s) => Write(ConsoleColor.Red, s);

    public static bool Check(string label, bool ok, string detail = "")
    {
        Write(ok ? ConsoleColor.Green : ConsoleColor.Red, $"  [{(ok ? "OK" : "SAI")}] {label}{(detail == "" ? "" : $"  ({detail})")}");
        return ok;
    }

    public static async Task<bool> ExpectOk(string label, Func<Task> action)
    {
        try { await action(); return Check(label, true); }
        catch (HubException ex) { return Check(label, false, ex.Message); }
    }

    // Lỗi do hub ném HubException → client nhận HubException kèm thông báo của server.
    // Thử lại tới khi đạt kết quả mong đợi (tối đa 10 giây), in ra mất bao lâu.
    // Dùng cho thay đổi lan truyền chậm (eventual consistency), vd thêm thành viên → chờ Kafka xóa cache.
    public static async Task<bool> Eventually(string label, bool expectOk, Func<Task> action, int timeoutMs = 10000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        string last = "";
        while (true)
        {
            bool ok;
            try { await action(); ok = true; last = "thành công"; }
            catch (HubException ex) { ok = false; last = ex.Message; }
            if (ok == expectOk) return Check(label, true, $"sau {sw.ElapsedMilliseconds} ms");
            if (sw.ElapsedMilliseconds > timeoutMs) return Check(label, false, $"quá {timeoutMs} ms, lần cuối: {last}");
            await Task.Delay(200);
        }
    }

    public static async Task<bool> ExpectHubError(string label, Func<Task> action)
    {
        try { await action(); return Check(label, false, "không bị từ chối!"); }
        catch (HubException ex) { return Check(label, true, ex.Message); }
    }

    public static void Result(bool ok) =>
        Write(ok ? ConsoleColor.Green : ConsoleColor.Red, ok ? "\nKẾT QUẢ: ĐÚNG" : "\nKẾT QUẢ: KHÔNG như mong đợi");

    static void Write(ConsoleColor color, string s)
    {
        var old = Console.ForegroundColor;
        Console.ForegroundColor = color;
        Console.WriteLine(s);
        Console.ForegroundColor = old;
    }
}
