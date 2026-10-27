// Node 14; dùng SignalR đã có trong Angular. Tự tạo user/nhóm; không xóa dữ liệu thật.
const http = require('http'), crypto = require('crypto');
const { execFileSync } = require('child_process');
const signalr = require('../frontend/chat-app/node_modules/@microsoft/signalr');
const hubs = [], heartbeats = [];
const root = require('path').resolve(__dirname, '..');
let checks = 0;
const delay = ms => new Promise(r => setTimeout(r, ms));
function docker(args, input) { return execFileSync('docker', args, { cwd: root, input, encoding: 'utf8', windowsHide: true, stdio: ['pipe', 'pipe', 'pipe'], timeout: 30000 }).trim(); }
function mongo(script) { return docker(['exec','-i','mongo-router','mongosh','--quiet','chat_history','--file','/dev/stdin'], "const chat=db.getSiblingDB('chat_history');\n" + script); }
function uuid() {
  const b = crypto.randomBytes(16); let time = BigInt(Date.now());
  for (let i = 5; i >= 0; i--) { b[i] = Number(time & 255n); time >>= 8n; }
  b[6] = (b[6] & 15) | 112; b[8] = (b[8] & 63) | 128;
  const h = b.toString('hex'); return [h.slice(0,8),h.slice(8,12),h.slice(12,16),h.slice(16,20),h.slice(20)].join('-');
}
function api(path, method = 'GET', body, user) {
  return new Promise((resolve, reject) => {
    const data = body === undefined ? null : JSON.stringify(body);
    const req = http.request('http://localhost:5000' + path, { method, headers: {
      ...(data ? { 'Content-Type':'application/json', 'Content-Length':Buffer.byteLength(data) } : {}),
      ...(user ? { Authorization: 'Bearer ' + user.accessToken } : {})
    } }, res => {
      let text = ''; res.on('data', d => text += d); res.on('end', () => {
        if (res.statusCode >= 400) { const error = new Error(method+' '+path+': '+res.statusCode); error.status = res.statusCode; reject(error); }
        else { try { resolve(text ? JSON.parse(text) : null); } catch (e) { reject(e); } }
      });
    });
    req.on('error', reject); req.setTimeout(20000, () => req.destroy(new Error('HTTP timeout')));
    if (data) req.write(data); req.end();
  });
}
function check(label, condition) { if (!condition) throw new Error('[FAIL] '+label); checks++; console.log('[OK] '+label); }
async function until(condition, timeout = 15000) {
  const start = Date.now();
  while (Date.now()-start < timeout) { if (await condition()) return; await delay(200); }
  throw new Error('Timeout: '+condition.toString());
}
async function rejected(label, action, status) {
  let failed = false; try { await action(); } catch (e) { if (status && e.status !== status) throw e; failed = true; }
  check(label, failed);
}
async function newUser(suffix) {
  const userName = 'mg'+Date.now().toString(36)+suffix, password='Passw0rd!';
  await api('/api/auth/register','POST',{userName,email:userName+'@test.local',displayName:'Mongo '+suffix,password});
  const auth = await api('/api/auth/login','POST',{userName,password});
  const me = await api('/api/users/me','GET',undefined,auth);
  await until(() => docker(['exec','postgres','psql','-U','chatapp','-d','group_db','-tAc',"select count(*) from user_snapshots where user_id='"+me.id+"'"]) === '1');
  return { ...auth, ...me };
}
async function connect(user, port) {
  const hub = new signalr.HubConnectionBuilder().withUrl('http://localhost:'+port+'/hubs/chat', {
    accessTokenFactory:()=>user.accessToken, skipNegotiation:true, transport:signalr.HttpTransportType.WebSockets
  }).configureLogging(signalr.LogLevel.None).build();
  hubs.push(hub); hub.received=[];
  hub.on('ReceiveMessage', m=>hub.received.push(m)); hub.on('PresenceSnapshot', p=>hub.activity=p);
  await hub.start(); await hub.invoke('Heartbeat');
  let busy=false;
  heartbeats.push(setInterval(async()=>{
    if(busy || hub.state!==signalr.HubConnectionState.Connected)return;
    busy=true; try { await hub.invoke('Heartbeat'); } catch {} finally { busy=false; }
  },10000));
  return hub;
}
const send = (hub, group, content, id=uuid()) => hub.invoke('SendMessage', id, group, content);
const history = (user, group, query='') => api('/api/chat/groups/'+group+'/messages'+query,'GET',undefined,user);
function values(node, name, output=[]) {
  if (!node || typeof node!=='object') return output;
  for (const [key,value] of Object.entries(node)) {
    if (key==='rejectedPlans' || key==='allPlansExecution') continue;
    if (key===name && typeof value==='string') output.push(value);
    if (typeof value==='object') values(value,name,output);
  }
  return output;
}
function shard(group) {
  return values(JSON.parse(mongo("print(JSON.stringify(chat.messages.find({groupId:'"+group+"'}).explain().queryPlanner.winningPlan))")),'shardName')[0];
}
function offsets() {
  return docker(['exec','kafka','/opt/kafka/bin/kafka-get-offsets.sh','--bootstrap-server','kafka:29092','--topic','chat.message-sent'])
    .split(/\r?\n/).map(line=>line.split(':')).filter(p=>p.length===3).map(p=>({partition:p[1],offset:Number(p[2])}));
}
async function setup() {
  const a=await newUser('a'), b=await newUser('b'), outsider=await newUser('x');
  const group=(await api('/api/groups','POST',{name:'Mongo tích hợp'},a)).id;
  await api('/api/groups/'+group+'/members','POST',{userId:b.id},a);
  const ca=await connect(a,5003), cb=await connect(b,5013);
  await ca.invoke('JoinGroup',group); await cb.invoke('JoinGroup',group);
  return {a,b,outsider,group,ca,cb};
}
async function persistence() {
  const {a,b,outsider,group,ca,cb}=await setup();
  let other;
  for(let i=0;i<20;i++) {
    other=(await api('/api/groups','POST',{name:'Mongo shard đối diện'},a)).id;
    if(shard(other)!==shard(group))break;
  }
  check('Hai conversation định tuyến đến hai shard khác nhau',shard(other)!==shard(group));
  const start=Date.parse('2026-10-26T00:00:00+07:00');
  const dates=[start-1,start,start+123,start+7*86400000-1,start+7*86400000];
  for(let i=0;i<1000;i++)dates.push(start+8*86400000+i);
  const rows=dates.map((time,i)=>({id:uuid(),groupId:group,senderId:a.id,senderName:a.displayName,content:'fixture '+(i+1),
    sequenceNumber:i+1,createdAt:new Date(time).toISOString(),createdAtTicks:(BigInt(time)*10000n+621355968000000000n+(i===2?4567n:0n)).toString()}));
  mongo('const rows='+JSON.stringify(rows)+';'+`
    const docs=rows.map(r=>{ const at=new Date(r.createdAt), local=new Date(+at+7*3600000);
      local.setUTCDate(local.getUTCDate()-(local.getUTCDay()+6)%7); local.setUTCHours(0,0,0,0);
      return {_id:r.id,groupId:r.groupId,senderId:r.senderId,senderName:r.senderName,content:r.content,
        sequenceNumber:NumberLong(String(r.sequenceNumber)),createdAt:at,createdAtTicks:NumberLong(r.createdAtTicks),weekStartUtc:new Date(+local-7*3600000)}; });
    chat.messages.insertMany(docs); chat.message_keys.insertMany(docs.map(m=>({_id:m._id,groupId:m.groupId,senderId:m.senderId})));
    chat.conversation_state.updateOne({_id:rows[0].groupId},{$max:{lastSeq:NumberLong(String(rows.length))},$setOnInsert:{hasPending:false,leaseUntil:new Date(0),leaseVersion:NumberLong('0')}},{upsert:true});
    print('seeded');`);
  const week=await history(b,group,'?weekStart=2026-10-26&limit=2');
  check('Tuần Việt Nam: trang mới nhất seq 3,4',JSON.stringify(week.map(m=>m.sequenceNumber))==='[3,4]');
  check('Giữ độ chính xác timestamp dưới millisecond',week[0].createdAt.includes('.1234567'));
  const older=await history(b,group,'?weekStart=2026-10-26&beforeSeq=3&limit=2');
  check('Trang tuần sau đúng seq 2, loại biên ngoài tuần',older.length===1 && older[0].sequenceNumber===2);
  await rejected('Ngày không phải thứ Hai → 400',()=>history(a,group,'?weekStart=2026-10-27'),400);
  await rejected('Ngày sai định dạng → 400',()=>history(a,group,'?weekStart=bad'),400);
  await rejected('Người ngoài không đọc lịch sử tuần',()=>history(outsider,group,'?weekStart=2026-10-26'),403);
  const explain=JSON.parse(mongo("print(JSON.stringify(chat.messages.find({groupId:'"+group+"',weekStartUtc:ISODate('2026-10-25T17:00:00Z')}).sort({sequenceNumber:-1}).limit(50).explain('executionStats')))"));
  check('Lọc tuần dùng group_week_sequence',values(explain.queryPlanner.winningPlan,'indexName').includes('group_week_sequence'));
  check('Chỉ đọc tối đa 10 document thay vì 1.005',explain.executionStats.totalDocsExamined<=10);
  console.log('EXPLAIN '+JSON.stringify({shard:shard(group),docsExamined:explain.executionStats.totalDocsExamined,returned:explain.executionStats.nReturned}));
  const initialOffsets=offsets();
  const id=uuid(), first=await send(ca,group,'Sau fixture',id);
  check('Counter tiếp tục từ MAX lịch sử',first.sequenceNumber===1006);
  const duplicate=await send(ca,group,'retry thay nội dung',id);
  check('Retry trả đúng tin cũ và nội dung cũ',JSON.stringify(first)===JSON.stringify(duplicate));
  await rejected('Cùng ID ở conversation/shard khác bị chặn',()=>send(ca,other,'collision',id));
  await rejected('User khác dùng ID bị chặn',()=>send(cb,group,'collision',id));
  check('Toàn cluster chỉ có một message',mongo("print(chat.messages.countDocuments({_id:'"+id+"'}))")==='1');
  const failId=uuid(), rejectedText='rollback-'+failId;
  mongo("print(JSON.stringify(chat.runCommand({collMod:'messages',validator:{content:{$ne:'"+rejectedText+"'}}})))");
  try {
    await rejected('Insert giữa transaction lỗi → không ACK thành công',()=>send(ca,group,rejectedText,failId));
    check('Rollback cả khóa, message và Outbox',mongo("print(chat.message_keys.countDocuments({_id:'"+failId+"'})+chat.messages.countDocuments({_id:'"+failId+"'})+chat.outbox_messages.countDocuments({messageId:'"+failId+"'}))")==='0');
  } finally { mongo("print(JSON.stringify(chat.runCommand({collMod:'messages',validator:{}})))"); }
  const after=await send(ca,group,'Sau rollback');
  check('Bộ đếm cũng rollback',after.sequenceNumber===1007);
  await until(()=>mongo("print(chat.outbox_messages.countDocuments({groupId:'"+group+"',processedAt:null}))")==='0');
  check('Outbox đã được Kafka ACK',true);
  const ca2=await connect(a,5013), sameId=uuid();
  const same=await Promise.all([send(ca,group,'cùng ID',sameId),send(ca2,group,'cùng ID',sameId)]);
  check('Hai replica đồng thời cùng ID → một kết quả',JSON.stringify(same[0])===JSON.stringify(same[1]));
  check('Chỉ một Outbox cho ID gửi đồng thời',mongo("print(chat.outbox_messages.countDocuments({messageId:'"+sameId+"'}))")==='1');
  const loadHubs=await Promise.all(Array.from({length:8},(_,i)=>connect(a,i%2?5003:5013)));
  const began=Date.now();
  const batches=await Promise.all(loadHubs.map(async(hub,index)=>{
    const sent=[]; for(let i=index;i<200;i+=loadHubs.length)sent.push(await send(hub,group,'parallel '+i)); return sent;
  }));
  const seqs=[].concat(...batches).map(m=>m.sequenceNumber).sort((a,b)=>a-b);
  check('200 tin song song: seq duy nhất và liên tiếp',new Set(seqs).size===200 && seqs[199]-seqs[0]===199);
  console.log('LOAD '+(Date.now()-began)+' ms');
  await until(()=>mongo("print(chat.outbox_messages.countDocuments({groupId:'"+group+"',processedAt:null}))")==='0',30000);
  mongo("print(JSON.stringify(chat.conversation_state.deleteOne({_id:'"+group+"'})))");
  const repaired=await send(ca,group,'Sau mất state');
  check('Mất state: phục hồi từ MAX, không mất lịch sử',repaired.sequenceNumber===seqs[199]+1);
  mongo("print(JSON.stringify(chat.conversation_state.updateOne({_id:'"+group+"'},{$set:{lastSeq:NumberLong('1')}})))");
  const raised=await send(ca,group,'Sau state bị lùi');
  check('State bị lùi: unique index bảo vệ, nâng floor rồi retry',raised.sequenceNumber===repaired.sequenceNumber+1);
  await until(()=>mongo("print(chat.outbox_messages.countDocuments({groupId:'"+group+"',processedAt:null}))")==='0',30000);
  const records=[];
  for(const end of offsets()) {
    const begin=initialOffsets.find(p=>p.partition===end.partition).offset;
    if(end.offset===begin)continue;
    const output=docker(['exec','kafka','/opt/kafka/bin/kafka-console-consumer.sh','--bootstrap-server','kafka:29092',
      '--topic','chat.message-sent','--partition',end.partition,'--offset',String(begin),'--max-messages',String(end.offset-begin),'--timeout-ms','5000']);
    for(const line of output.split(/\r?\n/))if(line.startsWith('{')) { const event=JSON.parse(line); if(event.groupId===group)records.push(event); }
  }
  check('Kafka nhận đủ 205 sự kiện mới, không tăng vì retry',new Set(records.map(e=>e.eventId)).size===205);
  check('Hai publisher giữ thứ tự sequence trên Kafka',records.every((e,i)=>i===0 || e.sequenceNumber>=records[i-1].sequenceNumber));
}
async function faults() {
  const {a,b,group,ca,cb}=await setup();
  await until(async()=>(await api('/api/notifications/unread','GET',undefined,b)).some(c=>c.groupId===group));
  try {
    docker(['compose','stop','kafka']);
    const msg=await send(ca,group,'Kafka tắt vẫn lưu');
    await until(()=>cb.received.some(m=>m.id===msg.id)); check('Kafka tắt: SignalR vẫn nhận tin',true);
    check('Kafka tắt: Outbox còn chờ',mongo("print(chat.outbox_messages.countDocuments({messageId:'"+msg.id+"',processedAt:null}))")==='1');
  } finally { docker(['compose','start','kafka']); }
  await until(()=>mongo("print(chat.outbox_messages.countDocuments({groupId:'"+group+"',processedAt:null}))")==='0',45000);
  check('Kafka sống lại: Outbox tự phát tiếp',true);
  await until(async()=>(await api('/api/notifications/unread','GET',undefined,b)).some(c=>c.groupId===group && c.unreadCount===1),45000);
  check('Notification còn sống và unread chỉ tăng một lần sau Kafka phục hồi',true);
  const id=uuid();
  try {
    docker(['compose','stop','mongo-router']);
    await rejected('Mongo tắt: gửi tin không báo thành công',()=>send(ca,group,'retry khi Mongo về',id));
    await rejected('Mongo tắt: lịch sử trả 503',()=>history(a,group),503);
  } finally { docker(['compose','start','mongo-router']); }
  await until(async()=>{try { await history(a,group); return true; } catch{return false;}});
  const recovered=await send(ca,group,'retry khi Mongo về',id);
  check('Mongo phục hồi: retry cùng ID thành công',recovered.id===id);
  check('Sau lỗi vẫn chỉ một tin',mongo("print(chat.messages.countDocuments({_id:'"+id+"'}))")==='1');
  let healthy;
  for(let i=0;i<20;i++) { healthy=(await api('/api/groups','POST',{name:'Đọc shard còn sống'},a)).id; if(shard(healthy)!==shard(group))break; }
  await send(ca,healthy,'Tin nằm trên shard còn sống');
  const deadShard=shard(group).replace('chat-','mongo-');
  try {
    docker(['compose','stop',deadShard]);
    await rejected('Shard chứa conversation tắt: trả 503 có giới hạn thời gian',()=>history(a,group),503);
    check('Conversation trên shard khác vẫn đọc được',(await history(a,healthy)).length===1);
  } finally { docker(['compose','start',deadShard]); }
  await until(async()=>{try{return (await history(a,group)).length===2;}catch{return false;}},30000);
  check('Shard sống lại: lịch sử không mất',true);
}
(process.argv.includes('--faults')?faults():persistence()).then(()=>console.log('PASS '+checks+' kiểm tra MongoDB')).catch(e=>{
  console.error(e.stack); process.exitCode=1;
}).finally(async()=>{ for(const timer of heartbeats)clearInterval(timer); await Promise.all(hubs.map(h=>h.stop().catch(()=>{}))); });
