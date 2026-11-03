// Node 14. API/signaling tests; browser media tests nằm video-call-ui-test.cjs.
const {json,api,uuid}=require('./attachment-test.cjs');
const fs=require('fs'),path=require('path'),{execFileSync}=require('child_process');
const signalr=require('../frontend/chat-app/node_modules/@microsoft/signalr');
const root=path.resolve(__dirname,'..'),started=new Date().toISOString(),hubs=[],timers=[];
let checks=0;
const delay=ms=>new Promise(r=>setTimeout(r,ms));
function check(label,value){if(!value)throw Error('[FAIL] '+label);checks++;console.log('[OK] '+label);}
async function until(fn,timeout=20000){const start=Date.now();while(Date.now()-start<timeout){if(await fn())return;await delay(200);}throw Error('Timeout: '+fn.toString());}
async function denied(label,fn){let error;try{await fn();}catch(e){error=e;}check(label,!!error);return error;}
async function user(suffix){const userName='ad'+Date.now().toString(36)+suffix,password='Passw0rd!';await json('/api/auth/register','POST',{userName,email:userName+'@test.local',displayName:'Admin test video '+suffix,password});const session=await json('/api/auth/login','POST',{userName,password});return{...session,...await json('/api/users/me','GET',undefined,session),password};}
async function connect(user,port){const h=new signalr.HubConnectionBuilder().withUrl('http://localhost:'+port+'/hubs/chat',{accessTokenFactory:()=>user.accessToken,skipNegotiation:true,transport:signalr.HttpTransportType.WebSockets}).configureLogging(signalr.LogLevel.None).build();h.calls=[];h.signals=[];h.on('VideoCallChanged',c=>h.calls.push(c));h.on('VideoCallSignal',s=>h.signals.push(s));await h.start();h.config=await h.invoke('GetVideoCallConfig');hubs.push(h);timers.push(setInterval(()=>{if(h.state===signalr.HubConnectionState.Connected)h.invoke('Heartbeat').catch(()=>{});},8000));return h;}
async function friend(a,b){await json('/api/friends/requests','POST',{userId:b.id},a);const result=await json('/api/friends/requests/'+a.id+'/accept','POST',{},b);await until(async()=>{try{await json('/api/groups/'+result.directGroupId,'GET',undefined,a);return true;}catch{return false;}});return result.directGroupId;}
async function apiTests(a,b,c,group){
  const ah=await connect(a,5003),bh=await connect(b,5013),b2=await connect(b,5003),ch=await connect(c,5013);
  check('Config trả đúng connection hiện tại và STUN',!!ah.config.connectionId&&ah.config.iceServers[0].urls.some(u=>u.startsWith('stun:'))&&ah.config.iceTransportPolicy==='all');
  await denied('Không tự gọi chính mình',()=>ah.invoke('StartVideoCall',uuid(),a.id));
  await denied('Không gọi người chưa là bạn',()=>ah.invoke('StartVideoCall',uuid(),c.id));
  const id=uuid(),call=await ah.invoke('StartVideoCall',id,b.id);
  check('Gọi đúng chat riêng, trạng thái Ringing',call.state==='Ringing'&&call.groupId===group&&call.callerConnectionId===ah.config.connectionId);
  await until(()=>bh.calls.some(x=>x.id===id)&&b2.calls.some(x=>x.id===id));check('Chuông tới mọi tab người nhận qua hai replica',true);
  check('Retry Start cùng ID không tạo cuộc gọi khác',(await ah.invoke('StartVideoCall',id,b.id)).version===call.version);
  await denied('Người gọi bận không gọi thêm',()=>ah.invoke('StartVideoCall',uuid(),b.id));
  await denied('Người nhận bận từ chối cuộc gọi thứ hai',()=>ch.invoke('StartVideoCall',uuid(),b.id));
  await denied('Người ngoài không nhận cuộc gọi',()=>ch.invoke('AcceptVideoCall',id));
  await denied('Chưa nhận không chuyển SDP',()=>ah.invoke('SendVideoCallSignal',id,'offer','v=0\r\n'));
  const accepts=await Promise.allSettled([bh.invoke('AcceptVideoCall',id),b2.invoke('AcceptVideoCall',id)]);
  check('Hai tab cùng nhận chỉ một tab thắng',accepts.filter(x=>x.status==='fulfilled').length===1);
  const accepted=accepts.find(x=>x.status==='fulfilled').value,selected=accepted.calleeConnectionId===bh.config.connectionId?bh:b2,other=selected===bh?b2:bh;
  check('Nhận cuộc gọi chọn đúng connection',accepted.state==='Connecting'&&[bh.config.connectionId,b2.config.connectionId].includes(accepted.calleeConnectionId));
  await denied('Tab không được chọn không kết thúc được',()=>other.invoke('EndVideoCall',id,'hangup'));
  await denied('Người ngoài không chuyển tín hiệu',()=>ch.invoke('SendVideoCallSignal',id,'ice',JSON.stringify({candidate:'candidate:test'})));
  await denied('Người nhận không tạo SDP offer',()=>selected.invoke('SendVideoCallSignal',id,'offer','v=0\r\n'));
  await ah.invoke('SendVideoCallSignal',id,'offer','v=0\r\ns=API test\r\n');
  await until(()=>selected.signals.some(s=>s.callId===id&&s.kind==='offer'));
  check('SDP chỉ tới tab được chọn',!other.signals.some(s=>s.callId===id));
  await selected.invoke('SendVideoCallSignal',id,'answer','v=0\r\ns=API answer\r\n');
  await until(()=>ah.signals.some(s=>s.callId===id&&s.kind==='answer'));check('SDP answer về người gọi',true);
  await denied('ICE JSON lỗi bị chặn',()=>ah.invoke('SendVideoCallSignal',id,'ice','{}'));
  await denied('SDP quá dài bị chặn',()=>ah.invoke('SendVideoCallSignal',id,'offer','v=0'+'x'.repeat(24500)));
  await ah.invoke('PulseVideoCall',id,true);const active=await selected.invoke('PulseVideoCall',id,true);
  check('Hai bên báo kết nối thì state Active',active.state==='Active'&&active.connectedAt>0);
  await ah.invoke('EndVideoCall',id,'hangup');await until(()=>selected.calls.some(c=>c.id===id&&c.state==='Ended'));
  check('Hangup báo bên kia và giải phóng busy',(await selected.invoke('GetCurrentVideoCall'))===null);
  await denied('Ended không được gửi tiếp SDP',()=>ah.invoke('SendVideoCallSignal',id,'ice',JSON.stringify({candidate:'x'})));
  const rejectId=uuid();await ah.invoke('StartVideoCall',rejectId,b.id);const rejected=await bh.invoke('EndVideoCall',rejectId,'declined');check('Từ chối cuộc gọi',rejected.reason==='declined');
  const cancelId=uuid();await ah.invoke('StartVideoCall',cancelId,b.id);check('Hủy trước khi nhận',(await ah.invoke('EndVideoCall',cancelId,'cancelled')).reason==='cancelled');
  const glare=await Promise.allSettled([ah.invoke('StartVideoCall',uuid(),b.id),bh.invoke('StartVideoCall',uuid(),a.id)]);
  check('Gọi chéo đồng thời chỉ một cuộc gọi',glare.filter(x=>x.status==='fulfilled').length===1);
  const won=glare.find(x=>x.status==='fulfilled').value;await (won.callerId===a.id?ah:bh).invoke('EndVideoCall',won.id,'cancelled');
  const disconnected=uuid();await ah.invoke('StartVideoCall',disconnected,b.id);await bh.invoke('AcceptVideoCall',disconnected);await bh.stop();
  await until(()=>ah.calls.some(c=>c.id===disconnected&&c.state==='Ended'));check('Ngắt đúng phiên nhận kết thúc cuộc gọi',true);
  const restored=await connect(b,5013);
  const noAnswer=uuid();await ah.invoke('StartVideoCall',noAnswer,b.id);
  const pulse=setInterval(()=>ah.invoke('PulseVideoCall',noAnswer,false).catch(()=>{}),5000);timers.push(pulse);
  await until(()=>ah.calls.some(c=>c.id===noAnswer&&c.reason==='no-answer'),35000);clearInterval(pulse);check('Không nhận sau 30s tự kết thúc',true);
  const lost=uuid();await ah.invoke('StartVideoCall',lost,b.id);await restored.invoke('AcceptVideoCall',lost);
  await json('/api/friends/'+b.id,'DELETE',undefined,a);
  await until(()=>ah.calls.some(c=>c.id===lost&&c.reason==='permission-changed'),12000);check('Hủy bạn kết thúc cuộc gọi đang mở',true);
  const newGroup=await friend(a,b);check('Kết bạn lại giữ mã conversation',newGroup===group);
  const locked=uuid();await ah.invoke('StartVideoCall',locked,b.id);await restored.invoke('AcceptVideoCall',locked);
  const credentials=Object.fromEntries(fs.readFileSync(path.join(root,'infra/admin-seed-credentials.local.txt'),'utf8').trim().split(/\r?\n/).map(s=>{const i=s.indexOf('=');return[s.slice(0,i),s.slice(i+1)];}));
  const admin=await json('/api/auth/login','POST',{userName:credentials.UserName,password:credentials.Password});
  let account=(await json('/api/admin/users/'+b.id,'GET',undefined,admin)).user;
  await json('/api/admin/users/'+b.id+'/lock','PUT',{isLocked:true,reason:'Video test',version:account.version},admin);
  await until(()=>ah.calls.some(c=>c.id===locked&&c.state==='Ended'),12000);
  check('Khóa tài khoản thu hồi cuộc gọi đang mở',(await ah.invoke('GetCurrentVideoCall'))===null);
  await denied('Người gọi không gọi tiếp được tài khoản bị khóa',()=>ah.invoke('StartVideoCall',uuid(),b.id));
  account=(await json('/api/admin/users/'+b.id,'GET',undefined,admin)).user;
  await json('/api/admin/users/'+b.id+'/lock','PUT',{isLocked:false,version:account.version},admin);
  await restored.stop();await b2.stop();
  await denied('Người nhận đã ngắt mọi tab không đổ chuông',()=>ah.invoke('StartVideoCall',uuid(),b.id));
  await Promise.all(hubs.map(h=>h.stop().catch(()=>{})));
  return {a,b,c,group,checks};
}
async function run(){
  try{
    const [a,b,c]=await Promise.all(['a','b','c'].map(user));const group=await friend(a,b);await friend(b,c);
    if(!process.argv.includes('--ui-only'))await apiTests(a,b,c,group);
    if(process.argv.includes('--ui')||process.argv.includes('--ui-only')){
      // API tests deliberately lock/unlock B, which revokes the original JWT.
      // Browser regression starts with fresh sessions, just as a user signs in again.
      for(const fixture of [a,b])Object.assign(fixture,await json('/api/auth/login','POST',{userName:fixture.userName,password:fixture.password}));
      await require('./video-call-ui-test.cjs').run({a,b,c,group});
    }
    if(checks)console.log('PASS '+checks+' video API checks');
  }finally{
    timers.forEach(clearInterval);await Promise.all(hubs.map(h=>h.stop().catch(()=>{})));
    execFileSync(process.execPath,[path.join(__dirname,'cleanup-test-data.cjs'),'--apply','--wait','--since='+started],{cwd:root,stdio:'inherit',windowsHide:true,timeout:240000});
  }
}
if(require.main===module)run().then(()=>process.exit(0),e=>{console.error(e.stack||e.message);process.exit(1);});
