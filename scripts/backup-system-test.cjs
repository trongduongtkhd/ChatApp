// Real source + isolated recovery web integration. Node 14, no new npm packages.
const http=require('http'),fs=require('fs'),path=require('path'),crypto=require('crypto');
const {execFileSync}=require('child_process');
const signalr=require('../frontend/chat-app/node_modules/@microsoft/signalr');
const root=path.resolve(__dirname,'..'),started=new Date().toISOString(),hubs=[];
const delay=ms=>new Promise(r=>setTimeout(r,ms));
let checks=0,backupId=null,context=null,writerRunning=false,writer=null;
function check(label,value){if(!value)throw Error('[FAIL] '+label);checks++;console.log('[OK] '+label);}
function uuid(){const b=crypto.randomBytes(16);b[6]=(b[6]&15)|64;b[8]=(b[8]&63)|128;const h=b.toString('hex');return[h.slice(0,8),h.slice(8,12),h.slice(12,16),h.slice(16,20),h.slice(20)].join('-');}
function docker(args,input){return execFileSync('docker',args,{cwd:root,input,encoding:'utf8',windowsHide:true,stdio:'pipe',timeout:180000}).trim();}
async function until(fn,timeout=120000){const end=Date.now()+timeout;while(Date.now()<end){if(await fn())return;await delay(500);}throw Error('Timed out waiting for integration step');}
function request(route,method='GET',body,user,port=5000,headers={}){return new Promise((resolve,reject)=>{
  const data=body===undefined?null:Buffer.isBuffer(body)?body:Buffer.from(JSON.stringify(body));
  const req=http.request({hostname:'127.0.0.1',port,path:route,method,headers:{...(data?{'content-type':Buffer.isBuffer(body)?'application/octet-stream':'application/json','content-length':data.length}:{}),...(user?{Authorization:'Bearer '+user.accessToken}:{}),...headers}},res=>{
    const chunks=[];res.on('data',d=>chunks.push(d));res.on('end',()=>{const raw=Buffer.concat(chunks);let value;try{value=JSON.parse(raw.toString());}catch{value=raw;}
      if(res.statusCode>=400){const e=Error(method+' '+route+': '+res.statusCode+' '+(value.title||raw.toString().slice(0,300)));e.status=res.statusCode;reject(e);}else resolve({value,raw,status:res.statusCode});});});
  req.on('error',reject);req.setTimeout(180000,()=>req.destroy(Error('HTTP timeout')));if(data)req.write(data);req.end();});}
const api=async(...args)=>(await request(...args)).value;
async function denied(label,fn,status){let caught;try{await fn();}catch(e){caught=e;}check(label,!!caught&&caught.status===status);}
async function createUser(suffix){const userName='ad'+Date.now().toString(36)+'backup'+suffix,password='Passw0rd!';
  await api('/api/auth/register','POST',{userName,email:userName+'@test.local',displayName:'Admin test backup '+suffix,password});
  const login=await api('/api/auth/login','POST',{userName,password});return {...login,...await api('/api/users/me','GET',undefined,login),userName,password};}
async function connect(user,port=5000){const h=new signalr.HubConnectionBuilder().withUrl('http://127.0.0.1:'+port+'/hubs/chat',{accessTokenFactory:()=>user.accessToken,skipNegotiation:true,transport:signalr.HttpTransportType.WebSockets}).configureLogging(signalr.LogLevel.None).build();await h.start();hubs.push(h);return h;}
async function history(user,group,port=5000){let all=[],before='';while(true){const rows=await api('/api/chat/groups/'+group+'/messages?limit=100'+before,'GET',undefined,user,port);all=rows.concat(all);if(rows.length<100)break;before='&beforeSeq='+rows[0].sequenceNumber;}return all;}

async function main(){
  const secrets=JSON.parse(fs.readFileSync(path.join(process.env.APPDATA,'Microsoft/UserSecrets/chatapp-dev/secrets.json'),'utf8').replace(/^\uFEFF/,''));
  const admin=await api('/api/auth/login','POST',{userName:'admin',password:secrets['AdminSeed:Password']});
  const adminMe=await api('/api/users/me','GET',undefined,admin);check('Existing admin login is valid',adminMe.systemRole==='SystemAdmin');
  if(process.argv.includes('--cleanup-recovery'))await api('/api/admin/backups/recovery','DELETE',undefined,admin);
  await denied('Anonymous cannot view backup control',()=>api('/api/admin/backups'),401);
  const a=await createUser('a'),b=await createUser('b');
  await denied('Regular account cannot create backups',()=>api('/api/admin/backups','POST',{},a),403);
  await delay(1200);
  const group=(await api('/api/groups','POST',{name:'Backup demo '+Date.now(),description:'Fixture for chapter 7'},a)).id;
  await api('/api/groups/'+group+'/members','POST',{userId:b.id},a);
  await delay(1200);
  const hub=await connect(a);await hub.invoke('JoinGroup',group);
  const first=await hub.invoke('SendMessage',uuid(),group,'A - saved before the full system backup');
  const fileBytes=Buffer.from('Chapter 7 - backup object bytes\nUTF-8: Xin chào');
  const uploadId=uuid();
  const attachment=await api('/api/chat/groups/'+group+'/attachments/'+uploadId,'PUT',fileBytes,a,5000,{'X-File-Name':'backup-demo.txt'});
  const attached=await hub.invoke('SendMessageWithAttachments',uuid(),group,'File saved before backup',[attachment.id]);
  check('Source attachment exists before backup',attached.attachments.length===1);
  let writes=0,reads=0,writeFailures=0,readFailures=0;
  writerRunning=true;
  writer=(async()=>{while(writerRunning){try{if(writes<80){await hub.invoke('SendMessage',uuid(),group,'Concurrent backup write '+writes);writes++;}await api('/api/users/me','GET',undefined,a);reads++;}catch(e){writeFailures++;console.error('Concurrent operation error: '+e.message);}await delay(250);}})();
  const overview=await api('/api/admin/backups','GET',undefined,admin);
  check('Source backup feature enabled',overview.enabled===true);
  backupId=(await api('/api/admin/backups','POST',{},admin)).id;
  let previous='';
  await until(async()=>{
    const status=(await api('/api/admin/backups','GET',undefined,admin)).items.find(x=>x.id===backupId);
    if(status&&status.message!==previous){previous=status.message;console.log('[PROGRESS] '+status.status+': '+status.message);}
    if(status&&status.status==='failed')throw Error('Backup failed: '+status.message);
    return status&&status.status==='complete';
  },1200000);
  writerRunning=false;await writer;
  check('Users could write during full backup',writes>=5&&writeFailures===0);
  check('Users could read during full backup',reads>=5&&readFailures===0);
  check('All files verify against system manifest',(await api('/api/admin/backups/'+backupId+'/verify','POST',{},admin)).verified);
  const directory=path.join(root,'infra/backups/system',backupId);
  const manifest=JSON.parse(fs.readFileSync(path.join(directory,'manifest.json'),'utf8'));
  check('Short coordinated checkpoint completed',manifest.checkpointMilliseconds>0&&manifest.checkpointMilliseconds<8000);
  check('Artifact contains PostgreSQL, Mongo, Kafka, objects and web',manifest.files.some(x=>x.path==='postgres/metadata.json')&&manifest.files.some(x=>x.path==='mongo/catalog.json')&&manifest.files.some(x=>x.path==='kafka/checkpoint.json')&&manifest.files.some(x=>x.path.startsWith('mongo/objects/'))&&manifest.files.some(x=>x.path==='web/index.html'));
  const after=await hub.invoke('SendMessage',uuid(),group,'AFTER BACKUP - must not appear in recovery');
  await api('/api/groups/'+group,'DELETE',undefined,a);
  context={backupId,groupId:group,fixtureUser:a.userName,fixturePassword:a.password,firstMessageId:first.id,attachmentId:attachment.id,checks,writes,reads,checkpointMs:manifest.checkpointMilliseconds,createdAt:new Date().toISOString()};
  fs.writeFileSync(path.join(root,'infra/backups/system/demo-context.local.json'),JSON.stringify(context,null,2));
  await denied('Deleted source group is unavailable',()=>api('/api/groups/'+group,'GET',undefined,a),404);
  await api('/api/admin/backups/'+backupId+'/restore','POST',{},admin);
  await until(async()=>{const state=(await api('/api/admin/backups','GET',undefined,admin)).recovery;if(state.status==='failed')throw Error('Restore failed: '+state.message);return state.status==='ready';},1200000);
  const restoredA=await api('/api/auth/login','POST',{userName:a.userName,password:a.password},undefined,4201);
  const restoredGroup=await api('/api/groups/'+group,'GET',undefined,restoredA,4201);
  check('Deleted group is restored on separate web',restoredGroup.group.id===group);
  const restoredMessages=await history(restoredA,group,4201);
  const expected=fs.readFileSync(path.join(directory,'mongo/messages.jsonl'),'utf8').trim().split(/\r?\n/).filter(Boolean).map(line=>JSON.parse(line)).filter(d=>d.groupId===group).map(d=>d._id).sort();
  check('Restored history exactly matches Mongo snapshot',JSON.stringify(restoredMessages.map(m=>m.id).sort())===JSON.stringify(expected));
  check('Message after checkpoint is excluded',!restoredMessages.some(m=>m.id===after.id));
  check('Before-checkpoint baseline messages survived',restoredMessages.some(m=>m.id===first.id)&&restoredMessages.some(m=>m.id===attached.id));
  const content=await request('/api/chat/groups/'+group+'/attachments/'+attachment.id+'/content','GET',undefined,restoredA,4201);
  check('Attachment bytes recovered exactly',content.raw.equals(fileBytes));
  check('Recovery mode is visible to web',(await api('/api/system/mode','GET',undefined,undefined,4201)).recoveryMode===true);
  const restoredAdmin=await api('/api/auth/login','POST',{userName:'admin',password:secrets['AdminSeed:Password']},undefined,4201);
  check('Recovered web cannot start backup jobs',(await api('/api/admin/backups','GET',undefined,restoredAdmin,4201)).enabled===false);
  await denied('Active recovery cannot be overwritten',()=>api('/api/admin/backups/'+backupId+'/restore','POST',{},admin),409);
  await denied('Source group stays deleted after recovery',()=>api('/api/groups/'+group,'GET',undefined,a),404);
  const rdb=docker(['exec','redis','redis-cli','CONFIG','GET','appendonly']);check('Source Redis AOF enabled',rdb.split(/\r?\n/).pop()==='yes');
  const replica=docker(['exec','postgres-standby','psql','-U','chatapp','-d','postgres','-tA','-c','SELECT pg_is_in_recovery();']);check('PostgreSQL replica remains a standby',replica==='t');
  const replicaUser=docker(['exec','postgres-standby','psql','-U','chatapp','-d','identity_db','-tA','-c',"SELECT count(*) FROM users WHERE id='"+a.id+"';"]);check('Replication copied the new fixture account',replicaUser==='1');
  const oldSettings=(await api('/api/admin/backups','GET',undefined,admin)).settings;
  await api('/api/admin/backups/settings','PUT',{period:'hourly',retentionCount:5},admin);
  check('Schedule persists through the API',(await api('/api/admin/backups','GET',undefined,admin)).settings.period==='hourly');
  await api('/api/admin/backups/settings','PUT',oldSettings,admin);
  context={backupId,groupId:group,fixtureUser:a.userName,fixturePassword:a.password,firstMessageId:first.id,attachmentId:attachment.id,checks,writes,reads,checkpointMs:manifest.checkpointMilliseconds,createdAt:new Date().toISOString()};
  fs.writeFileSync(path.join(root,'infra/backups/system/demo-context.local.json'),JSON.stringify(context,null,2));
  fs.writeFileSync(path.join(directory,'integration-test-report.json'),JSON.stringify({checks,passed:true,writes,reads,checkpointMs:manifest.checkpointMilliseconds},null,2));
  console.log('PASS '+checks+' backup integration checks; checkpoint '+manifest.checkpointMilliseconds.toFixed(0)+' ms; '+writes+' writes during backup.');
}
module.exports={api,request,check,until,delay,docker,connect,hubs};
if(require.main===module)main().catch(e=>{console.error(e.stack);process.exitCode=1;}).finally(async()=>{
  writerRunning=false;if(writer)await writer.catch(()=>{});for(const hub of hubs)await hub.stop().catch(()=>{});
  // Normal cleanup uses the existing scoped fixture cleaner, including object bytes.
  if(!process.argv.includes('--keep-fixtures')){
    try{const output=execFileSync(process.execPath,[path.join(__dirname,'cleanup-test-data.cjs'),'--apply','--wait','--since='+started],{cwd:root,encoding:'utf8',windowsHide:true,stdio:'pipe',timeout:180000});console.log('[CLEANUP] Source fixtures cleaned; retained-data fingerprints verified.');}
    catch(e){console.error('Fixture cleanup failed: '+String(e.stderr||e.message).slice(0,1000));process.exitCode=1;}
  }
});
