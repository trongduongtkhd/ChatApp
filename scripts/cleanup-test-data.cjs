// Mặc định chỉ xem trước. --apply backup rồi xóa đúng fixture; --since giới hạn một lượt test.
const fs=require('fs'),path=require('path'),crypto=require('crypto');
const {execFileSync}=require('child_process');
const root=path.resolve(__dirname,'..');
const sinceArg=process.argv.find(a=>a.startsWith('--since='));
const since=sinceArg?Date.parse(sinceArg.slice(8)):null;
if(sinceArg&&!Number.isFinite(since))throw new Error('--since cần thời điểm ISO hợp lệ.');
function docker(args,input){return execFileSync('docker',args,{cwd:root,input,encoding:'utf8',windowsHide:true,stdio:['pipe','pipe','pipe'],timeout:120000}).trim();}
function pg(database,sql){return docker(['exec','-i','postgres','psql','-U','chatapp','-d',database,'-tA','-v','ON_ERROR_STOP=1'],sql);}
function rows(database,sql){return JSON.parse(pg(database,'SELECT COALESCE(json_agg(row_to_json(x)),\'[]\'::json) FROM ('+sql+') x;'));}
function mongo(script){return docker(['exec','-i','mongo-router','mongosh','--quiet','chat_history','--file','/dev/stdin'],script);}
function fixtureUser(u){
  if(u.system_role==='SystemAdmin'||u.user_name==='admin')return false;
  const name=u.user_name,display=u.display_name,email=u.email.toLowerCase();
  if(email===name.toLowerCase()+'@test.local'){
    if(/^fr?\d{6}[a-z0-9]+$/i.test(name))return true;
    if(/^mg[a-z0-9]+$/i.test(name)&&display.startsWith('Mongo '))return true;
    if(/^ad[a-z0-9]+$/i.test(name)&&display.startsWith('Admin test '))return true;
    if(/^au[a-z0-9]+$/i.test(name)&&display.startsWith('UI quản trị '))return true;
    if(/^ui[a-z0-9]+$/i.test(name)&&display.startsWith('Presence UI '))return true;
  }
  if(email===name.toLowerCase()+'@t.local'&&(/^ui\d{6}[a-z]$/i.test(name)||/^u7\d{6}[a-z]$/i.test(name))&&/Thử|Bạn/.test(display))return true;
  if(/^demo_\d{6}$/.test(name)&&email===name+'@test.com'&&display==='Demo Outbox')return true;
  if(/^kafka_(an|binh|chi|dung|em)$/.test(name)&&email===name+'@test.com'&&/Kafka/.test(display))return true;
  return false;
}
const demoNames=new Set(['Demo gRPC','Demo chat','Demo group-service chết','Demo gửi trùng','Demo 200 tin',
  'Demo mất bộ đếm','Demo cache thành viên','Demo group-service chết (có cache)','Demo group-service chết (chưa cache)',
  'Demo presence','Demo lịch sử','Demo 503','Demo 502','Demo backplane','Demo load balancing','Demo failover',
  'Demo đảo thứ tự sự kiện','Demo unread','Demo sự kiện trùng']);
function inspect(){
  const users=rows('identity_db','SELECT id,user_name,email,display_name,created_at,system_role FROM users ORDER BY id');
  const groups=rows('group_db','SELECT id,name,description,owner_id,is_direct,created_at FROM groups ORDER BY id');
  const members=rows('group_db','SELECT group_id,user_id,role FROM group_members ORDER BY group_id,user_id');
  const targets=users.filter(u=>fixtureUser(u)&&(since===null||Date.parse(u.created_at)>=since)),ids=new Set(targets.map(u=>u.id));
  const groupsToRemove=groups.filter(g=>ids.has(g.owner_id)||(!g.is_direct&&demoNames.has(g.name)&&(since===null||Date.parse(g.created_at)>=since)));
  const groupIds=new Set(groupsToRemove.map(g=>g.id));
  const mixed=groupsToRemove.filter(g=>ids.has(g.owner_id)&&members.some(m=>m.group_id===g.id&&!ids.has(m.user_id)));
  const retained=users.filter(u=>!ids.has(u.id));
  const summary={usersTotal:users.length,testUsers:targets.length,groupsTotal:groups.length,testGroups:groupsToRemove.length,
    mixedGroups:mixed.map(g=>({id:g.id,name:g.name})),
    retainedUsers:retained.map(u=>({id:u.id,userName:u.user_name,email:u.email,displayName:u.display_name})),
    remainingGroups:groups.filter(g=>!groupIds.has(g.id)).map(g=>({id:g.id,name:g.name,ownerId:g.owner_id,isDirect:g.is_direct}))};
  return {users,groups,members,targets,groupsToRemove,summary};
}
function uuidArray(ids){
  if(ids.some(id=>!/^[a-f0-9-]{36}$/.test(id)))throw new Error('Invalid UUID in cleanup manifest');
  return "ARRAY["+ids.map(id=>"'"+id+"'::uuid").join(',')+"]::uuid[]";
}
function scope(userIds,groupIds){
  const u=uuidArray(userIds),g=uuidArray(groupIds);
  return {u,g,identity:`key = ANY(${u}::text[]) OR payload->>'userId' = ANY(${u}::text[]) OR payload->>'userLowId' = ANY(${u}::text[]) OR payload->>'userHighId' = ANY(${u}::text[])`,
    group:`key = ANY(${g}::text[]) OR payload->>'userId' = ANY(${u}::text[])`,
    chat:`key = ANY(${g}::text[]) OR payload->>'senderId' = ANY(${u}::text[])`};
}
function buildPlan(){
  const data=inspect();
  if(data.summary.mixedGroups.length)throw new Error('Có nhóm chủ test nhưng có thành viên thật; cần kiểm tra thủ công.');
  const userIds=data.targets.map(u=>u.id),groupIds=data.groupsToRemove.map(g=>g.id),known=new Set(data.groups.map(g=>g.id));
  const historyGroups=JSON.parse(mongo(`print(JSON.stringify(db.messages.aggregate([{$group:{_id:'$groupId',senders:{$addToSet:'$senderId'}}}]).toArray()))`));
  const legacyGroups=rows('chat_db','SELECT group_id AS id,array_agg(DISTINCT sender_id::text) AS senders FROM messages GROUP BY group_id');
  const users=new Set(userIds),removed=new Set(groupIds);
  for(const item of historyGroups.map(g=>({id:g._id,senders:g.senders})).concat(legacyGroups)){
    if(!known.has(item.id)&&item.senders.length&&item.senders.every(id=>users.has(id)))removed.add(item.id);
  }
  const allGroups=Array.from(removed).sort(),s=scope(userIds,allGroups);
  const eventIds=[];
  for(const [database,filter] of [['identity_db',s.identity],['group_db',s.group],['chat_db',s.chat]])
    eventIds.push(...rows(database,'SELECT id FROM outbox_messages WHERE '+filter).map(x=>x.id));
  const mongoScope=JSON.parse(mongo(`const u=${JSON.stringify(userIds)},g=${JSON.stringify(allGroups)};
    const filter={$or:[{groupId:{$in:g}},{senderId:{$in:u}}]};
    const ids=db.messages.find(filter,{_id:1}).toArray().map(m=>m._id);
    const out={$or:[{groupId:{$in:g}},{messageId:{$in:ids}}]};
    print(JSON.stringify({messageIds:ids,eventIds:db.outbox_messages.find(out,{_id:1}).toArray().map(e=>e._id),
      pending:db.outbox_messages.countDocuments({$and:[out,{processedAt:null}]})}));`));
  for(const [database,filter] of [['identity_db',s.identity],['group_db',s.group],['chat_db',s.chat]]){
    if(Number(pg(database,'SELECT count(*) FROM outbox_messages WHERE processed_at IS NULL AND ('+filter+');')))
      throw new Error('Outbox test chưa phát hết ở '+database+'; chờ xử lý xong trước khi dọn.');
  }
  if(mongoScope.pending)throw new Error('Mongo Outbox test chưa phát hết.');
  eventIds.push(...mongoScope.eventIds);
  const plan={createdAt:new Date().toISOString(),userIds,groupIds:allGroups,eventIds:Array.from(new Set(eventIds)),messageIds:mongoScope.messageIds,
    users:data.targets.map(u=>({id:u.id,userName:u.user_name,displayName:u.display_name})),groups:data.groupsToRemove,
    retainedUserIds:data.users.filter(u=>!users.has(u.id)).map(u=>u.id),
    retainedGroupIds:data.groups.filter(g=>!removed.has(g.id)).map(g=>g.id)};
  console.log(JSON.stringify({...data.summary,orphanTestGroups:allGroups.length-data.groupsToRemove.length,
    mongoMessagesToRemove:plan.messageIds.length,eventsToRemove:plan.eventIds.length},null,2));
  return plan;
}
function retainedFingerprint(plan){
  const u=uuidArray(plan.userIds),g=uuidArray(plan.groupIds);
  const checks={};
  for(const [database,table,filter,order] of [
    ['identity_db','users',`NOT(id = ANY(${u}))`,'id'],
    ['identity_db','friendships',`NOT(user_low_id = ANY(${u}) OR user_high_id = ANY(${u}))`,'user_low_id,user_high_id'],
    ['group_db','groups',`NOT(id = ANY(${g}))`,'id'],
    ['group_db','group_members',`NOT(group_id = ANY(${g}) OR user_id = ANY(${u}))`,'group_id,user_id'],
    ['chat_db','messages',`NOT(group_id = ANY(${g}) OR sender_id = ANY(${u}))`,'id']]){
    checks[database+'.'+table]=pg(database,`SELECT md5(COALESCE(string_agg(row_to_json(t)::text,''),'empty')) FROM (SELECT * FROM ${table} WHERE ${filter} ORDER BY ${order}) t;`);
  }
  checks.mongo=mongo(`const crypto=require('crypto'),hash=crypto.createHash('sha256');
    db.messages.find({groupId:{$nin:${JSON.stringify(plan.groupIds)}},senderId:{$nin:${JSON.stringify(plan.userIds)}}}).sort({_id:1}).forEach(d=>hash.update(EJSON.stringify(d)));
    print(hash.digest('hex'));`);
  return checks;
}
function backup(stamp){
  const dir=path.join(root,'infra','backups');fs.mkdirSync(dir,{recursive:true});
  for(const database of ['identity_db','group_db','notification_db','chat_db']){
    const name='test-cleanup-'+stamp+'-'+database+'.dump';
    docker(['exec','postgres','pg_dump','-U','chatapp','-d',database,'-Fc','-f','/tmp/'+name]);
    docker(['cp','postgres:/tmp/'+name,path.join(dir,name)]);
  }
  const name='test-cleanup-'+stamp+'-mongo.dump';
  docker(['exec','mongo-router','mongodump','--db=chat_history','--archive=/tmp/'+name,'--gzip']);
  docker(['cp','mongo-router:/tmp/'+name,path.join(dir,name)]);
  return dir;
}
function apply(plan){
  if(!plan.userIds.length&&!plan.groupIds.length){console.log('Không còn fixture phù hợp; không thay đổi dữ liệu.');return;}
  const before=retainedFingerprint(plan),s=scope(plan.userIds,plan.groupIds),e=uuidArray(plan.eventIds);
  const stamp=new Date().toISOString().replace(/[-:]/g,'').replace(/\.\d{3}Z$/,'Z');
  const dir=backup(stamp);
  fs.writeFileSync(path.join(dir,'test-cleanup-'+stamp+'.json'),JSON.stringify({plan,before},null,2));
  console.log('Backup hoàn tất: infra/backups/test-cleanup-'+stamp+'-*');
  pg('group_db',`BEGIN;
    DELETE FROM outbox_messages WHERE ${s.group};
    DELETE FROM group_join_requests WHERE group_id = ANY(${s.g}) OR user_id = ANY(${s.u}) OR requested_by = ANY(${s.u});
    DELETE FROM group_audits WHERE group_id = ANY(${s.g}) OR actor_id = ANY(${s.u}) OR target_id = ANY(${s.u});
    DELETE FROM group_members WHERE group_id = ANY(${s.g}) OR user_id = ANY(${s.u});
    DELETE FROM groups WHERE id = ANY(${s.g});
    DELETE FROM user_snapshots WHERE user_id = ANY(${s.u});
    COMMIT;`);
  pg('notification_db',`BEGIN;
    DELETE FROM unread_counters WHERE group_id = ANY(${s.g}) OR user_id = ANY(${s.u});
    DELETE FROM group_member_snapshots WHERE group_id = ANY(${s.g}) OR user_id = ANY(${s.u});
    DELETE FROM processed_events WHERE event_id = ANY(${e});
    COMMIT;`);
  pg('chat_db',`BEGIN;
    DELETE FROM outbox_messages WHERE ${s.chat};
    DELETE FROM messages WHERE group_id = ANY(${s.g}) OR sender_id = ANY(${s.u});
    COMMIT;`);
  const mongoResult=mongo(`const u=${JSON.stringify(plan.userIds)},g=${JSON.stringify(plan.groupIds)},ids=${JSON.stringify(plan.messageIds)};
    print(JSON.stringify({messages:db.messages.deleteMany({$or:[{groupId:{$in:g}},{senderId:{$in:u}}]}).deletedCount,
      keys:db.message_keys.deleteMany({$or:[{groupId:{$in:g}},{senderId:{$in:u}}]}).deletedCount,
      outbox:db.outbox_messages.deleteMany({$or:[{groupId:{$in:g}},{messageId:{$in:ids}}]}).deletedCount,
      states:db.conversation_state.deleteMany({_id:{$in:g}}).deletedCount}));`);
  pg('identity_db',`BEGIN;
    DELETE FROM outbox_messages WHERE ${s.identity};
    DELETE FROM account_audits WHERE target_id = ANY(${s.u}) OR actor_id = ANY(${s.u});
    DELETE FROM friendships WHERE user_low_id = ANY(${s.u}) OR user_high_id = ANY(${s.u});
    DELETE FROM users WHERE id = ANY(${s.u}) AND system_role != 'SystemAdmin' AND user_name != 'admin';
    COMMIT;`);
  // Xóa đúng key/mục thành viên test; giữ nguyên dữ liệu Redis của người thật.
  const redisScript=`local users=cjson.decode(ARGV[1]); local groups=cjson.decode(ARGV[2]); local n=0;
    for _,id in ipairs(users) do n=n+redis.call('DEL','presence:v2:sessions:'..id,'presence:'..id);redis.call('ZREM','presence:v2:due',id);redis.call('SREM','presence:v2:online',id);redis.call('PUBLISH','chatapp:presence:changed',id);end
    for _,id in ipairs(groups) do n=n+redis.call('DEL','group:members:'..id,'chat:seq:'..id);end
    redis.call('PUBLISH','chatapp:membership:changed','cleanup');return n;`;
  docker(['exec','redis','redis-cli','EVAL',redisScript,'0',JSON.stringify(plan.userIds),JSON.stringify(plan.groupIds)]);
  const after=retainedFingerprint(plan);
  if(JSON.stringify(before)!==JSON.stringify(after))throw new Error('Fingerprint dữ liệu giữ lại thay đổi; cần đối chiếu backup.');
  const final=inspect();
  console.log('Mongo đã dọn: '+mongoResult);
  console.log('Đối chiếu dữ liệu giữ lại: KHỚP trước/sau.');
  console.log(JSON.stringify(final.summary,null,2));
  fs.writeFileSync(path.join(dir,'test-cleanup-'+stamp+'-result.json'),JSON.stringify({mongoResult,before,after,summary:final.summary},null,2));
}
function assertConsumersCaughtUp(){
  const output=docker(['exec','kafka','/opt/kafka/bin/kafka-consumer-groups.sh','--bootstrap-server','kafka:29092','--all-groups','--describe']);
  const rows=output.split(/\r?\n/).map(s=>s.trim().split(/\s+/)).filter(p=>['chat-service','group-service','notification-service'].includes(p[0]));
  if(!rows.length||rows.some(p=>!/^\d+$/.test(p[5])||Number(p[5])!==0))throw new Error('Kafka consumers chưa xử lý hết; chờ trước khi dọn.');
}
async function main(){
  const deadline=Date.now()+(process.argv.includes('--wait')?60000:0);
  let plan;
  while(true){
    try{
      if(process.argv.includes('--apply'))assertConsumersCaughtUp();
      plan=buildPlan();break;
    }catch(error){
      if(Date.now()>=deadline||!/Outbox.*chưa|Kafka consumers chưa/.test(error.message))throw error;
      await new Promise(r=>setTimeout(r,500));
    }
  }
  if(process.argv.includes('--apply'))apply(plan);
  else console.log('Chỉ xem trước. --apply sẽ backup rồi xóa đúng các ID đã xác định ở trên.');
}
main().catch(error=>{console.error(error.message);process.exitCode=1;});
