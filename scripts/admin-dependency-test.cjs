// Node 14. Fixtures riêng; không xóa user/nhóm/tin thật, không in token hoặc mật khẩu seed.
const http=require('http'), fs=require('fs'), path=require('path'), crypto=require('crypto');
const {execFileSync,spawn}=require('child_process');
const signalr=require('../frontend/chat-app/node_modules/@microsoft/signalr');
const root=path.resolve(__dirname,'..'), hubs=[], timers=[];
let checks=0;
const delay=ms=>new Promise(r=>setTimeout(r,ms));
function check(label, value){if(!value)throw new Error('[FAIL] '+label);checks++;console.log('[OK] '+label);}
async function until(condition,timeout=20000){const start=Date.now();while(Date.now()-start<timeout){if(await condition())return;await delay(200);}throw new Error('Timeout: '+condition.toString());}
function docker(args){return execFileSync('docker',args,{cwd:root,encoding:'utf8',windowsHide:true,stdio:'pipe',timeout:45000}).trim();}
function sql(database,query){return docker(['exec','postgres','psql','-U','chatapp','-d',database,'-tAc',query]);}
function api(url,method='GET',body,user,port=5000){return new Promise((resolve,reject)=>{
  const data=body===undefined?null:JSON.stringify(body);
  const req=http.request('http://localhost:'+port+url,{method,headers:{...(data?{'Content-Type':'application/json','Content-Length':Buffer.byteLength(data)}:{}),...(user?{Authorization:'Bearer '+user.accessToken}:{})}},res=>{
    let text='';res.on('data',d=>text+=d);res.on('end',()=>{let value;try{value=text?JSON.parse(text):null;}catch{value=text;}
      if(res.statusCode>=400){const e=new Error(method+' '+url+' → '+res.statusCode+' '+(value?.title||''));e.status=res.statusCode;reject(e);}else resolve(value);
    });});req.on('error',reject);req.setTimeout(15000,()=>req.destroy(new Error('HTTP timeout')));if(data)req.write(data);req.end();});}
async function denied(label,action,status){let error;try{await action();}catch(e){error=e;}if(error&&status&&error.status!==status)throw error;check(label,!!error&&(!status||error.status===status));}
function uuid(){const b=crypto.randomBytes(16);let n=BigInt(Date.now());for(let i=5;i>=0;i--){b[i]=Number(n&255n);n>>=8n;}b[6]=(b[6]&15)|112;b[8]=(b[8]&63)|128;const h=b.toString('hex');return[h.slice(0,8),h.slice(8,12),h.slice(12,16),h.slice(16,20),h.slice(20)].join('-');}
async function user(suffix){const userName='ad'+Date.now().toString(36)+suffix,password='Passw0rd!';await api('/api/auth/register','POST',{userName,email:userName+'@test.local',displayName:'Admin test '+suffix,password,systemRole:'SystemAdmin'});const login=await api('/api/auth/login','POST',{userName,password});const me=await api('/api/users/me','GET',undefined,login);await until(()=>sql('group_db',"select count(*) from user_snapshots where user_id='"+me.id+"'")==='1',60000);return{...me,...login,password};}
async function connect(user,port=5003,notification=false){const hub=new signalr.HubConnectionBuilder().withUrl('http://localhost:'+port+'/hubs/'+(notification?'notifications':'chat'),{accessTokenFactory:()=>user.accessToken,skipNegotiation:true,transport:signalr.HttpTransportType.WebSockets}).configureLogging(signalr.LogLevel.None).build();hubs.push(hub);hub.received=[];hub.on('ReceiveMessage',m=>hub.received.push(m));await hub.start();if(!notification){await hub.invoke('Heartbeat');timers.push(setInterval(()=>{if(hub.state===signalr.HubConnectionState.Connected)hub.invoke('Heartbeat').catch(()=>{});},10000));}return hub;}

function localService(name,start){
  if(!['Identity','Group'].includes(name))throw new Error('Unexpected service');
  const exe='ChatApp.'+name+'Service.exe', project='backend/src/Services/'+name+'/ChatApp.'+name+'Service';
  if(start){
    const output=fs.openSync(path.join(root,'.admin-'+name+'.log'),'a'),error=fs.openSync(path.join(root,'.admin-'+name+'.err.log'),'a');
    const child=spawn('dotnet',['run','--no-build','--project',project],{cwd:root,windowsHide:true,detached:true,stdio:['ignore',output,error]});
    child.unref();fs.closeSync(output);fs.closeSync(error);return;
  }
  const command=`Get-CimInstance Win32_Process | Where-Object { $_.Name -eq '${exe}' -and $_.ExecutablePath.StartsWith('${root}' + [IO.Path]::DirectorySeparatorChar) } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force }`;
  execFileSync('powershell',['-NoProfile','-Command',command],{cwd:root,encoding:'utf8',stdio:'pipe',windowsHide:true,timeout:15000});
}
async function ready(port){await until(async()=>{try{await api('/','GET',undefined,undefined,port);return true;}catch{return false;}},30000);}
async function main(){
  const a=await user('depa'), b=await user('depb');
  const group=(await api('/api/groups','POST',{name:'Dependency failure fixture'},a)).id;
  await api('/api/groups/'+group+'/members','POST',{userId:b.id},a);
  const ca=await connect(a,5003),cb=await connect(b,5013),nb=await connect(b,5004,true);
  await ca.invoke('JoinGroup',group);await cb.invoke('JoinGroup',group);
  const baseline=await ca.invoke('SendMessage',uuid(),group,'Trước dependency outage');
  try{
    localService('Identity',false);
    await denied('Identity tắt: Group REST trả 503',async()=>api('/api/groups','GET',undefined,a),503);
    await denied('Identity tắt: Chat history trả 503',async()=>api('/api/chat/groups/'+group+'/messages','GET',undefined,a),503);
    await denied('Identity tắt: Notification REST trả 503',async()=>api('/api/notifications/unread','GET',undefined,a),503);
    await denied('Identity tắt: hub không nhận gửi mới',()=>ca.invoke('SendMessage',uuid(),group,'Không được lưu'));
    await until(()=>[ca,cb,nb].every(h=>h.state===signalr.HubConnectionState.Disconnected),15000);
    check('Không xác nhận được account: server đóng cả hai Chat và Notification',true);
  }finally{localService('Identity',true);await ready(5001);}
  check('Identity phục hồi: token hợp lệ vẫn dùng được',(await api('/api/users/me','GET',undefined,a)).id===a.id);
  // REST root lên trước khi channel gRPC đang backoff của Chat phục hồi; kiểm cả đường quyền thật.
  await until(async()=>{try{await api('/api/chat/groups/'+group+'/messages','GET',undefined,a,5003);return true;}catch{return false;}},30000);
  const fresh=await connect(a,5003);await fresh.invoke('JoinGroup',group);
  const retryId=uuid();
  try{
    localService('Group',false);
    await denied('Group tắt: history trả 503',async()=>api('/api/chat/groups/'+group+'/messages','GET',undefined,a),503);
    await denied('Group tắt: không dùng cache cũ để gửi',()=>fresh.invoke('SendMessage',retryId,group,'Gửi lại sau phục hồi'));
    check('Group outage không làm account bị khóa',(await api('/api/users/me','GET',undefined,a)).id===a.id);
  }finally{localService('Group',true);await ready(5002);}
  await until(async()=>{try{await api('/api/chat/groups/'+group+'/messages','GET',undefined,a,5003);return true;}catch{return false;}},30000);
  const after=await fresh.invoke('SendMessage',retryId,group,'Gửi lại sau phục hồi');
  check('Group phục hồi: gửi lại cùng ID thành công',after.id===retryId&&after.sequenceNumber===baseline.sequenceNumber+1);
  const history=await api('/api/chat/groups/'+group+'/messages','GET',undefined,a);
  check('Outage không mất tin cũ hoặc tạo tin trùng',history.length===2&&history.some(m=>m.id===baseline.id));
  await user('resumed');
  check('Consumer Group đã đồng bộ lại sau restart',true);
  console.log('PASS '+checks+' kiểm tra dependency/fail closed/recovery.');
}
main().catch(e=>{console.error(e.stack);process.exitCode=1;}).finally(async()=>{for(const t of timers)clearInterval(t);await Promise.all(hubs.map(h=>h.stop().catch(()=>{})));});
