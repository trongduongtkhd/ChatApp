// Node 14. Fixtures riêng; không xóa user/nhóm/tin thật, không in token hoặc mật khẩu seed.
const http=require('http'), fs=require('fs'), path=require('path'), crypto=require('crypto');
const {execFileSync}=require('child_process');
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
async function main(){
  const credentials=Object.fromEntries(fs.readFileSync(path.join(root,'infra/admin-seed-credentials.local.txt'),'utf8').trim().split(/\r?\n/).map(s=>{const i=s.indexOf('=');return[s.slice(0,i),s.slice(i+1)];}));
  const admin=await api('/api/auth/login','POST',{userName:credentials.UserName,password:credentials.Password});
  const me=await api('/api/users/me','GET',undefined,admin);
  check('Seed tạo SystemAdmin đăng nhập được',me.systemRole==='SystemAdmin');
  const [a,b,c,d,e,f,g,h,z]=await Promise.all(['a','b','c','d','e','f','g','h','z'].map(user));
  check('Đăng ký không tự cấp SystemAdmin dù client gửi role',a.systemRole==='User');
  await denied('Chưa đăng nhập không vào API admin',async()=>api('/api/admin/users'),401);
  await denied('User thường không vào admin users',async()=>api('/api/admin/users','GET',undefined,a),403);
  await denied('User thường không vào admin groups',async()=>api('/api/admin/groups','GET',undefined,a),403);
  await denied('Gọi thẳng Identity vẫn kiểm quyền admin',async()=>api('/api/admin/users','GET',undefined,a,5001),403);
  const p1=await api('/api/admin/users?page=1&pageSize=2','GET',undefined,admin),p2=await api('/api/admin/users?page=2&pageSize=2','GET',undefined,admin);
  check('Admin phân trang ổn định, hai trang không trùng',p1.items.length===2&&p2.items.length===2&&!p1.items.some(x=>p2.items.some(y=>y.id===x.id)));
  for(const q of[a.userName,a.displayName,a.email])check('Tìm người dùng bằng '+(q===a.email?'email':q===a.userName?'tài khoản':'tên hiển thị'),(await api('/api/admin/users?q='+encodeURIComponent(q),'GET',undefined,admin)).items.some(u=>u.id===a.id));
  check('DTO admin không lộ PasswordHash',!JSON.stringify(p1).toLowerCase().includes('password'));
  await denied('Phân trang sai bị chặn',async()=>api('/api/admin/users?page=0','GET',undefined,admin),400);
  const group=(await api('/api/groups','POST',{name:'Governance '+Date.now(),description:'Fixture quản trị'},a)).id;
  const base='/api/groups/'+group;
  const detail=async(actor=a)=>await api(base,'GET',undefined,actor);
  const version=async(actor=a)=>(await detail(actor)).group.version;
  await api(base+'/members','POST',{userId:b.id},a);await api(base+'/members','POST',{userId:c.id},a);
  check('Nhóm cũ/mới mặc định tắt phê duyệt',(await detail()).group.requiresApproval===false);
  await denied('Member không tự bổ nhiệm Admin',async()=>api(base+'/members/'+c.id+'/role','PUT',{role:'Admin',version:0},c),403);
  await api(base+'/members/'+b.id+'/role','PUT',{role:'Admin',version:await version()},a);
  check('Owner bổ nhiệm Admin',(await detail(b)).group.myRole==='Admin');
  check('Đổi role không phát dư member-added',sql('group_db',"select count(*) from outbox_messages where key='"+group+"' and topic='group.member-added'")==='3');
  await denied('Admin không bổ nhiệm Admin khác',async()=>api(base+'/members/'+c.id+'/role','PUT',{role:'Admin',version:await version(b)},b),403);
  await denied('Admin không sửa cài đặt approval',async()=>api(base+'/approval','PUT',{requiresApproval:true,version:await version(b)},b),403);
  const oldVersion=await version();
  await api(base+'/approval','PUT',{requiresApproval:true,version:oldVersion},a);
  await denied('Cài đặt version cũ → 409',async()=>api(base+'/approval','PUT',{requiresApproval:false,version:oldVersion},a),409);
  const pending=await api(base+'/members','POST',{userId:d.id},c);
  check('Member thêm người khi bật approval → Pending',pending.status==='Pending'&&pending.userId===d.id);
  const repeated=await api(base+'/members','POST',{userId:d.id},c);
  check('Gửi yêu cầu lặp giữ cùng request',repeated.id===pending.id);
  check('Chưa duyệt không nằm trong group_members',!(await detail()).members.some(m=>m.userId===d.id));
  await denied('Pending không đọc danh sách thành viên',()=>detail(d),403);
  await denied('Pending không đọc lịch sử',async()=>api('/api/chat/groups/'+group+'/messages','GET',undefined,d),403);
  await denied('Member không đọc danh sách yêu cầu',async()=>api(base+'/join-requests','GET',undefined,c),403);
  await denied('Member không duyệt được',async()=>api(base+'/join-requests/'+pending.id,'PUT',{approve:true},c),403);
  const pendingHub=await connect(d);await denied('Pending không vào phòng SignalR',()=>pendingHub.invoke('JoinGroup',group));
  await Promise.all([api(base+'/join-requests/'+pending.id,'PUT',{approve:true},a),api(base+'/join-requests/'+pending.id,'PUT',{approve:true},b)]);
  check('Hai người cùng duyệt → một thành viên',sql('group_db',"select count(*) from group_members where group_id='"+group+"' and user_id='"+d.id+"'")==='1');
  check('Hai lần duyệt chỉ phát một member-added',sql('group_db',"select count(*) from outbox_messages where key='"+group+"' and topic='group.member-added' and payload->>'userId'='"+d.id+"'")==='1');
  await pendingHub.invoke('JoinGroup',group);check('Duyệt xong vào phòng ngay không chờ Kafka',true);
  const invite=await api(base+'/invite','POST',{version:await version(b)},b);
  check('Invite token ngẫu nhiên 192 bit',/^[a-f0-9]{48}$/.test(invite.token));
  check('DB chỉ giữ hash của invite',sql('group_db',"select length(invite_token_hash) from groups where id='"+group+"'")==='64');
  await denied('Preview invite cần đăng nhập',async()=>api('/api/groups/invites/'+invite.token),401);
  const preview=await api('/api/groups/invites/'+invite.token,'GET',undefined,e);
  check('Preview chỉ hiện metadata tối thiểu',preview.name.startsWith('Governance')&&!('members'in preview)&&!('ownerId'in preview));
  const ep=await api('/api/groups/invites/'+invite.token+'/join','POST',{},e);
  const race=await Promise.allSettled([api(base+'/join-requests/'+ep.id,'PUT',{approve:true},a),api(base+'/join-requests/'+ep.id,'PUT',{approve:false},b)]);
  check('Duyệt và từ chối đồng thời chỉ một hướng thắng',race.filter(x=>x.status==='fulfilled').length===1&&race.some(x=>x.status==='rejected'&&x.reason.status===409));
  await api('/api/groups/invites/'+invite.token+'/join','POST',{},f);
  await denied('Không hủy hộ request người khác',async()=>api(base+'/join-requests/mine','DELETE',undefined,z),404);
  await api(base+'/join-requests/mine','DELETE',undefined,f);
  check('Người gửi tự hủy request',!((await api(base+'/join-requests','GET',undefined,a)).some(r=>r.userId===f.id)));
  const fp=await api('/api/groups/invites/'+invite.token+'/join','POST',{},f);
  check('Sau hủy có thể gửi yêu cầu mới',fp.status==='Pending');
  const rotated=await api(base+'/invite','POST',{version:await version()},a);
  check('Rotate sinh token mới',rotated.token!==invite.token);
  await denied('Link cũ hết hiệu lực',async()=>api('/api/groups/invites/'+invite.token,'GET',undefined,g),404);
  check('Rotate giữ Pending đã có',(await api(base+'/join-requests','GET',undefined,a)).some(r=>r.id===fp.id));
  await api(base+'/approval','PUT',{requiresApproval:false,version:await version()},a);
  check('Tắt approval giữ yêu cầu cũ chờ duyệt',(await api(base+'/join-requests','GET',undefined,a)).some(r=>r.id===fp.id));
  const auto=await api('/api/groups/invites/'+rotated.token+'/join','POST',{},g);
  check('Tắt approval, link thêm trực tiếp',auto.role==='Member');
  const added=await api(base+'/members','POST',{userId:h.id},c);
  check('Tắt approval, Member thêm trực tiếp',added.role==='Member');
  await api(base+'/approval','PUT',{requiresApproval:true,version:await version()},a);
  const direct=await api(base+'/members','POST',{userId:z.id},b);
  check('Admin thêm trực tiếp dù bật approval',direct.role==='Member');
  await denied('Admin không xóa trưởng nhóm',async()=>api(base+'/members/'+a.id,'DELETE',undefined,b),400);
  await api(base+'/members/'+c.id+'/role','PUT',{role:'Admin',version:await version()},a);
  await denied('Admin không xóa Admin khác',async()=>api(base+'/members/'+c.id,'DELETE',undefined,b),403);
  await api(base+'/members/'+c.id+'/role','PUT',{role:'Member',version:await version()},a);
  await api(base+'/owner','PUT',{userId:c.id,version:await version()},a);
  const transferred=await detail(c);
  check('Chuyển Owner cập nhật OwnerId và role',transferred.group.ownerId===c.id&&transferred.members.find(m=>m.userId===c.id).role==='Owner'&&transferred.members.find(m=>m.userId===a.id).role==='Member');
  check('DB chỉ có đúng một Owner',sql('group_db',"select count(*) from group_members where group_id='"+group+"' and role='Owner'")==='1');
  await denied('Owner cũ mất quyền bổ nhiệm ngay',async()=>api(base+'/members/'+b.id+'/role','PUT',{role:'Member',version:await version(a)},a),403);
  await denied('Owner không tự rời trước chuyển quyền',async()=>api(base+'/members/'+c.id,'DELETE',undefined,c),400);
  const adminGroup=await api('/api/admin/groups/'+group,'GET',undefined,admin);
  check('SystemAdmin xem thành viên/role dù không ở nhóm',adminGroup.members.total===transferred.members.length&&adminGroup.members.items.some(m=>m.role==='Admin'));
  await denied('Xem admin không tự cấp quyền chat nhóm',async()=>api('/api/chat/groups/'+group+'/messages','GET',undefined,admin),403);
  await denied('SystemAdmin không tự được sửa nghiệp vụ nhóm',async()=>api(base+'/approval','PUT',{requiresApproval:false,version:transferred.group.version},admin),403);
  await api('/api/friends/requests','POST',{userId:b.id},a);
  const friend=await api('/api/friends/requests/'+a.id+'/accept','POST',{},b);
  const directId=friend.directGroupId;
  await until(async()=> (await api('/api/groups','GET',undefined,a)).some(g=>g.id===directId));
  for(const endpoint of ['/approval','/owner','/invite']){
    const body=endpoint==='/approval'?{requiresApproval:true,version:0}:endpoint==='/owner'?{userId:b.id,version:0}:{version:0};
    await denied('Chat riêng chặn '+endpoint,async()=>api('/api/groups/'+directId+endpoint,endpoint==='/invite'?'POST':'PUT',body,a),400);
  }
  await denied('Chat riêng chặn bổ nhiệm Admin',async()=>api('/api/groups/'+directId+'/members/'+b.id+'/role','PUT',{role:'Admin',version:0},a),400);
  await api(base+'/members/'+d.id+'/role','PUT',{role:'Admin',version:await version(c)},c);
  const ca=await connect(c,5003),cd1=await connect(d,5003),cd2=await connect(d,5013),notification=await connect(d,5004,true),cz=await connect(z,5013);
  const cg=await connect(g,5013);clearInterval(timers.pop());cg.revoked=false;
  cg.on('GroupAccessRevoked',id=>{if(id===group)cg.revoked=true;});await cg.invoke('JoinGroup',group);
  await Promise.all([ca,cd1,cd2,cz].map(x=>x.invoke('JoinGroup',group)));
  const msg=await cd1.invoke('SendMessage',uuid(),group,'Tin được giữ khi khóa tài khoản');
  let lockedUser;
  try{
    docker(['compose','stop','kafka']);
    await api(base+'/members/'+g.id,'DELETE',undefined,b);
    await denied('Kafka tắt, người bị gỡ không đọc lịch sử',async()=>api('/api/chat/groups/'+group+'/messages','GET',undefined,g),403);
    await denied('Kafka tắt, connection bị gỡ không gửi được',()=>cg.invoke('SendMessage',uuid(),group,'Không được gửi sau khi bị gỡ'));
    await until(()=>cg.revoked,15000);
    check('Không heartbeat/Kafka, server vẫn thu hồi phòng người bị gỡ',true);
    const target=(await api('/api/admin/users/'+d.id,'GET',undefined,admin)).user;
    await denied('Khóa cần lý do',async()=>api('/api/admin/users/'+d.id+'/lock','PUT',{isLocked:true,version:target.version,reason:''},admin),400);
    lockedUser=await api('/api/admin/users/'+d.id+'/lock','PUT',{isLocked:true,version:target.version,reason:'Fixture kiểm thử khóa'},admin);
    check('Khóa khi Kafka ngừng vẫn commit SQL',lockedUser.isLocked===true);
    await api(base+'/members/'+d.id+'/role','PUT',{role:'Member',version:await version(c)},c);
    check('Owner vẫn miễn nhiệm được Admin có tài khoản đang khóa',(await detail(c)).members.find(m=>m.userId===d.id).role==='Member');
    await denied('Không bổ nhiệm lại Admin khi tài khoản đang khóa',async()=>api(base+'/members/'+d.id+'/role','PUT',{role:'Admin',version:await version(c)},c),400);
    await denied('Token cũ gọi REST qua Gateway → 401',async()=>api(base,'GET',undefined,d),401);
    await denied('Gọi thẳng Group bằng token bị khóa → 401',async()=>api(base,'GET',undefined,d,5002),401);
    await denied('Tài khoản khóa không đăng nhập mới',async()=>api('/api/auth/login','POST',{userName:d.userName,password:d.password}),401);
    await denied('Connection cũ không tiếp tục gửi',()=>cd1.invoke('SendMessage',uuid(),group,'Không được lưu'));
    await until(()=>[pendingHub,cd1,cd2,notification].every(x=>x.state===signalr.HubConnectionState.Disconnected),15000);
    check('Server thu hồi mọi tab trên hai Chat và Notification',true);
    check('Thu hồi lease Presence của tài khoản',docker(['exec','redis','redis-cli','ZCARD','presence:v2:sessions:'+d.id])==='0');
    const healthy=await cz.invoke('SendMessage',uuid(),group,'Người khác vẫn chat khi Kafka tắt');
    check('Khóa một người không chặn người khác',healthy.sequenceNumber>msg.sequenceNumber);
    await delay(300);check('Đã thu hồi phòng thì không nhận tin mới',!cg.received.some(m=>m.id===healthy.id));
    await denied('Version cũ không ghi đè trạng thái khóa',async()=>api('/api/admin/users/'+d.id+'/lock','PUT',{isLocked:false,version:target.version},admin),409);
    await api('/api/admin/users/'+d.id+'/lock','PUT',{isLocked:false,version:lockedUser.version,reason:'Hoàn tất fixture'},admin);
    await denied('Mở khóa không hồi sinh token cũ',async()=>api('/api/users/me','GET',undefined,d),401);
    const fresh=await api('/api/auth/login','POST',{userName:d.userName,password:d.password});
    check('Mở khóa đăng nhập mới được',!!fresh.accessToken);
    check('Khóa/mở khóa giữ lịch sử',(await api('/api/chat/groups/'+group+'/messages','GET',undefined,fresh)).some(m=>m.id===msg.id));
    check('Audit có cả khóa và mở khóa',(await api('/api/admin/users/'+d.id,'GET',undefined,admin)).audit.length===2);
  }finally{docker(['compose','start','kafka']);if(lockedUser){const current=(await api('/api/admin/users/'+d.id,'GET',undefined,admin)).user;if(current.isLocked)await api('/api/admin/users/'+d.id+'/lock','PUT',{isLocked:false,version:current.version,reason:'Dọn fixture'},admin);}}
  await until(()=>sql('identity_db',"select count(*) from outbox_messages where topic='identity.account-state-changed' and key='"+d.id+"' and processed_at is null")==='0',60000);
  check('Outbox account-state gửi bù sau Kafka phục hồi',true);
  const adminDetail=(await api('/api/admin/users/'+me.id,'GET',undefined,admin)).user;
  await denied('Admin không tự khóa mình',async()=>api('/api/admin/users/'+me.id+'/lock','PUT',{isLocked:true,version:adminDetail.version,reason:'Không được'},admin),400);
  console.log('PASS '+checks+' kiểm tra quản trị/role/approval/link/revocation.');
}
main().catch(e=>{console.error(e.stack);process.exitCode=1;}).finally(async()=>{for(const t of timers)clearInterval(t);await Promise.all(hubs.map(h=>h.stop().catch(()=>{})));});
