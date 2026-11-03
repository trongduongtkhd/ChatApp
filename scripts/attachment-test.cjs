// Node 14; fixtures riêng, finally dọn cả DB và object. Không in token/secrets.
const http=require('http'),crypto=require('crypto'),path=require('path'),fs=require('fs');
const {execFileSync}=require('child_process');
const signalr=require('../frontend/chat-app/node_modules/@microsoft/signalr');
const root=path.resolve(__dirname,'..'),started=new Date().toISOString(),hubs=[];
let checks=0,storageStopped=false;
const delay=ms=>new Promise(r=>setTimeout(r,ms));
function check(label,value){if(!value)throw Error('[FAIL] '+label);console.log('[OK] '+label);checks++;}
function uuid(){const b=crypto.randomBytes(16);b[6]=(b[6]&15)|64;b[8]=(b[8]&63)|128;const h=b.toString('hex');return [h.slice(0,8),h.slice(8,12),h.slice(12,16),h.slice(16,20),h.slice(20)].join('-');}
function docker(args,input){return execFileSync('docker',args,{cwd:root,input,encoding:'utf8',windowsHide:true,stdio:'pipe',timeout:120000}).trim();}
function mongo(code){return docker(['exec','-i','mongo-router','mongosh','--quiet','chat_history','--file','/dev/stdin'],code);}
async function until(fn,timeout=30000){const t=Date.now();while(Date.now()-t<timeout){if(await fn())return;await delay(250);}throw Error('Timeout: '+fn.toString());}
function api(route,method='GET',body,user,port=5000,headers={}){return new Promise((resolve,reject)=>{
  const data=body===undefined?null:Buffer.isBuffer(body)?body:Buffer.from(JSON.stringify(body));
  const req=http.request({hostname:'localhost',port,path:route,method,headers:{...(data?{'Content-Type':Buffer.isBuffer(body)?'application/octet-stream':'application/json','Content-Length':data.length}:{}),...(user?{Authorization:'Bearer '+user.accessToken}:{}),...headers}},res=>{
    const chunks=[];res.on('data',d=>chunks.push(d));res.on('end',()=>{const raw=Buffer.concat(chunks);let value;try{value=JSON.parse(raw.toString());}catch{value=raw;}
      if(res.statusCode>=400){const e=Error(method+' '+route+' '+res.statusCode+' '+(value.title||raw.toString().slice(0,100)));e.status=res.statusCode;reject(e);}else resolve({value,raw,headers:res.headers,status:res.statusCode});});});
  req.on('error',reject);req.setTimeout(120000,()=>req.destroy(Error('HTTP timeout')));if(data)req.write(data);req.end();});}
const json=async(...args)=>(await api(...args)).value;
async function denied(label,fn,status){let e;try{await fn();}catch(error){e=error;}if(e&&status&&e.status!==status)throw e;check(label,!!e&&(!status||e.status===status));}
async function user(suffix){const userName='ad'+Date.now().toString(36)+suffix,password='Passw0rd!';await json('/api/auth/register','POST',{userName,email:userName+'@test.local',displayName:'Admin test attachment '+suffix,password});const login=await json('/api/auth/login','POST',{userName,password});return{...login,...await json('/api/users/me','GET',undefined,login),password};}
async function connect(user,port){const h=new signalr.HubConnectionBuilder().withUrl('http://localhost:'+port+'/hubs/chat',{accessTokenFactory:()=>user.accessToken,skipNegotiation:true,transport:signalr.HttpTransportType.WebSockets}).configureLogging(signalr.LogLevel.None).build();h.received=[];h.on('ReceiveMessage',m=>h.received.push(m));await h.start();hubs.push(h);return h;}
function testPng(){
  const zlib=require('zlib'),width=240,height=120,raw=Buffer.alloc(height*(width*4+1));
  for(let y=0;y<height;y++)for(let x=0;x<width;x++){const i=y*(width*4+1)+1+x*4;raw[i]=30;raw[i+1]=100+Math.floor(y/2);raw[i+2]=180+Math.floor(x/4);raw[i+3]=255;}
  function chunk(type,data){const body=Buffer.concat([Buffer.from(type),data]);let crc=0xffffffff;for(const b of body){crc^=b;for(let j=0;j<8;j++)crc=(crc>>>1)^((crc&1)?0xedb88320:0);}const out=Buffer.alloc(data.length+12);out.writeUInt32BE(data.length);body.copy(out,4);out.writeUInt32BE((crc^0xffffffff)>>>0,out.length-4);return out;}
  const ihdr=Buffer.alloc(13);ihdr.writeUInt32BE(width);ihdr.writeUInt32BE(height,4);ihdr[8]=8;ihdr[9]=6;
  return Buffer.concat([Buffer.from([137,80,78,71,13,10,26,10]),chunk('IHDR',ihdr),chunk('IDAT',zlib.deflateSync(raw)),chunk('IEND',Buffer.alloc(0))]);
}
const png=testPng();
async function main(){
  const [a,b,c]=await Promise.all(['a','b','c'].map(user));
  await delay(1500);
  const group=(await json('/api/groups','POST',{name:'Attachment test '+Date.now()},a)).id;
  await json('/api/groups/'+group+'/members','POST',{userId:b.id},a);
  const other=(await json('/api/groups','POST',{name:'Attachment other '+Date.now()},a)).id;
  const base='/api/chat/groups/'+group+'/attachments';
  const upload=async(name,bytes,id=uuid(),user=a,port=5000,g=group)=>json('/api/chat/groups/'+g+'/attachments/'+id,'PUT',bytes,user,port,{'X-File-Name':encodeURIComponent(name)});
  const ah=await connect(a,5003),bh=await connect(b,5013);await ah.invoke('JoinGroup',group);await bh.invoke('JoinGroup',group);
  const photo=await upload('Ảnh thử.png',png),fileBytes=Buffer.from('Báo cáo ứng dụng phân tán\nUTF-8'),file=await upload('Báo cáo.txt',fileBytes);
  check('Upload ảnh qua Gateway, định dạng do server xác định',photo.kind==='image'&&photo.contentType==='image/png'&&photo.size===png.length);
  check('Upload file UTF-8, metadata không lộ objectKey',file.kind==='file'&&!('objectKey'in file));
  check('Retry upload cùng ID và nội dung trả cùng file',(await upload(file.fileName,fileBytes,file.id)).id===file.id);
  await denied('ID upload không đổi được nội dung',()=>upload(file.fileName,Buffer.from('khác'),file.id),409);
  await denied('Không đăng nhập không upload',()=>upload('a.txt',Buffer.from('a'),uuid(),null),401);
  await denied('Người ngoài không upload',()=>upload('a.txt',Buffer.from('a'),uuid(),c),403);
  await denied('Ảnh giả đổi đuôi bị chặn',()=>upload('fake.png',Buffer.from('not an image')),400);
  await denied('File thực thi bị chặn',()=>upload('a.exe',Buffer.from('MZ')),400);
  await denied('File rỗng bị chặn',()=>upload('empty.txt',Buffer.alloc(0)),413);
  await denied('File vượt 25 MiB bị chặn',()=>upload('large.txt',Buffer.alloc(25*1024*1024+1,65)),413);
  await denied('Chưa gửi không được tải nội dung',()=>api(base+'/'+photo.id+'/content','GET',undefined,a),404);
  await denied('Không lấy upload của người khác',()=>json(base+'/'+photo.id,'GET',undefined,b),403);
  const mid=uuid(),m=await ah.invoke('SendMessageWithAttachments',mid,group,'Chú thích ảnh và file',[photo.id,file.id]);
  await until(()=>bh.received.some(x=>x.id===mid));
  check('Tin nhiều file qua hai Chat replica',m.attachments.length===2&&bh.received.find(x=>x.id===mid).attachments.length===2);
  check('Retry gửi không nhân đôi',(await ah.invoke('SendMessageWithAttachments',mid,group,'Chú thích ảnh và file',[photo.id,file.id])).sequenceNumber===m.sequenceNumber);
  const bytes=await api(base+'/'+file.id+'/content','GET',undefined,b,5013);
  check('Tải khác replica, nội dung giữ nguyên',bytes.raw.equals(fileBytes));
  check('Tải file có disposition và không cache công khai',bytes.headers['content-disposition'].startsWith('attachment')&&bytes.headers['cache-control'].includes('no-store'));
  check('Tải ảnh đúng MIME',(await api(base+'/'+photo.id+'/content','GET',undefined,b)).headers['content-type'].startsWith('image/png'));
  await denied('Người ngoài không tải file',()=>api(base+'/'+file.id+'/content','GET',undefined,c),403);
  await denied('Gửi lại attachment bằng tin khác bị chặn',()=>ah.invoke('SendMessageWithAttachments',uuid(),group,'',[file.id]));
  const foreign=await upload('foreign.txt',Buffer.from('foreign'),uuid(),a,5000,other);
  await denied('File khác conversation không được gắn',()=>ah.invoke('SendMessageWithAttachments',uuid(),group,'',[foreign.id]));
  const owned=await upload('owned.txt',Buffer.from('owned'),uuid(),b);
  await denied('Không dùng file upload của người khác',()=>ah.invoke('SendMessageWithAttachments',uuid(),group,'',[owned.id]));
  await denied('Không gửi quá 5 attachment',()=>ah.invoke('SendMessageWithAttachments',uuid(),group,'',Array(6).fill(owned.id)));
  const race=await upload('race.txt',Buffer.from('race'));
  const raceHub=await connect(a,5013);
  const races=await Promise.allSettled([ah.invoke('SendMessageWithAttachments',uuid(),group,'',[race.id]),raceHub.invoke('SendMessageWithAttachments',uuid(),group,'',[race.id])]);
  check('Hai tin tranh một file chỉ một tin thành công',races.filter(x=>x.status==='fulfilled').length===1);
  const conflict=await upload('send-vs-cancel.txt',Buffer.from('send versus cancel'));
  const conflictResults=await Promise.allSettled([ah.invoke('SendMessageWithAttachments',uuid(),group,'',[conflict.id]),json(base+'/'+conflict.id,'DELETE',undefined,a)]);
  check('Gửi và hủy cùng lúc chỉ một hướng thắng',conflictResults.filter(x=>x.status==='fulfilled').length===1);
  const cancelled=await upload('cancel.txt',Buffer.from('cancel'));
  await json(base+'/'+cancelled.id,'DELETE',undefined,a);
  await denied('File đã hủy không gửi được',()=>ah.invoke('SendMessageWithAttachments',uuid(),group,'',[cancelled.id]));
  await until(()=>mongo(`print(db.attachments.findOne({_id:'${cancelled.id}'}).state)`)==='Deleted');
  check('Worker dọn file hủy',true);
  const expired=await upload('expired.txt',Buffer.from('expired'));
  mongo(`db.attachments.updateOne({_id:'${expired.id}'},{$set:{expiresAt:new Date(0)}});db.attachments.updateOne({_id:'${file.id}'},{$set:{expiresAt:new Date(0)}});`);
  await denied('Upload hết hạn không gửi được',()=>ah.invoke('SendMessageWithAttachments',uuid(),group,'',[expired.id]));
  await until(()=>mongo(`print(db.attachments.findOne({_id:'${expired.id}'}).state)`)==='Deleted');
  check('Worker không dọn file đã gửi',(await api(base+'/'+file.id+'/content','GET',undefined,b)).raw.equals(fileBytes));
  const big=await Promise.all([1,2,3].map(i=>upload('large'+i+'.txt',Buffer.alloc(18*1024*1024,65))));
  await denied('Tổng trên 50 MiB rollback cả tin và attachment',()=>ah.invoke('SendMessageWithAttachments',uuid(),group,'',big.map(f=>f.id)));
  check('Rollback giữ các upload Ready',await Promise.all(big.map(f=>json(base+'/'+f.id,'GET',undefined,a))).then(x=>x.every(s=>s.state==='Ready')));
  const now=new Date(Date.now()+7*3600000);const day=now.getUTCDay(),monday=new Date(now);monday.setUTCDate(now.getUTCDate()-((day+6)%7));
  check('Lịch sử tuần chứa metadata đính kèm',(await json('/api/chat/groups/'+group+'/messages?weekStart='+monday.toISOString().slice(0,10),'GET',undefined,b)).some(x=>x.id===mid&&x.attachments.length===2));
  await bh.stop();const offline=await upload('offline.txt',Buffer.from('offline'));
  const off=await ah.invoke('SendMessageWithAttachments',uuid(),group,'',[offline.id]);
  check('Offline lấy bù ảnh/file qua REST',(await json('/api/chat/groups/'+group+'/messages','GET',undefined,b)).some(x=>x.id===off.id&&x.attachments.length===1));
  docker(['compose','stop','attachment-storage']);storageStopped=true;
  await denied('Kho dừng thì download trả lỗi tạm thời',()=>api(base+'/'+file.id+'/content','GET',undefined,b),503);
  check('Kho dừng vẫn gửi chữ',(await ah.invoke('SendMessage',uuid(),group,'Text when storage down')).content==='Text when storage down');
  docker(['compose','up','-d','--force-recreate','attachment-storage']);storageStopped=false;
  await until(async()=>{try{return(await api(base+'/'+file.id+'/content','GET',undefined,b)).raw.equals(fileBytes);}catch{return false;}},60000);
  check('Tạo lại container vẫn giữ file trong volume',true);
  await json('/api/friends/requests','POST',{userId:b.id},a);
  const friend=await json('/api/friends/requests/'+a.id+'/accept','POST',{},b),direct=friend.directGroupId;
  await until(async()=>{try{await json('/api/groups/'+direct,'GET',undefined,a);return true;}catch{return false;}});
  const directFile=await upload('private.txt',Buffer.from('private'),uuid(),a,5000,direct);
  await ah.invoke('JoinGroup',direct);
  const dm=await ah.invoke('SendMessageWithAttachments',uuid(),direct,'',[directFile.id]);
  check('Chat riêng gửi file và xem lịch sử',(await json('/api/chat/groups/'+direct+'/messages','GET',undefined,b)).some(x=>x.id===dm.id));
  const directUrl='/api/chat/groups/'+direct+'/attachments/'+directFile.id+'/content';
  check('Người kia tải file chat riêng',(await api(directUrl,'GET',undefined,b)).raw.toString()==='private');
  await json('/api/friends/'+b.id,'DELETE',undefined,a);
  await until(async()=>{try{await api(directUrl,'GET',undefined,b);return false;}catch(e){return e.status===403;}});
  check('Hủy bạn mất quyền tải mới',true);
  await json('/api/groups/'+group+'/members/'+b.id,'DELETE',undefined,a);
  await denied('Gỡ thành viên thu hồi quyền tải',()=>api(base+'/'+file.id+'/content','GET',undefined,b),403);
  await json('/api/groups/'+group+'/members','POST',{userId:b.id},a);
  const detail=await json('/api/groups/'+group,'GET',undefined,a);
  await json('/api/groups/'+group+'/approval','PUT',{requiresApproval:true,version:detail.group.version},a);
  const pending=await json('/api/groups/'+group+'/members','POST',{userId:c.id},b);
  check('Member đề xuất thêm người vào nhóm có phê duyệt',pending.status==='Pending');
  await denied('Pending không tải đính kèm',()=>api(base+'/'+file.id+'/content','GET',undefined,c),403);
  const credentials=Object.fromEntries(fs.readFileSync(path.join(root,'infra/admin-seed-credentials.local.txt'),'utf8').trim().split(/\r?\n/).map(s=>{const i=s.indexOf('=');return[s.slice(0,i),s.slice(i+1)];}));
  const admin=await json('/api/auth/login','POST',{userName:credentials.UserName,password:credentials.Password});
  await json('/api/groups/'+group+'/members','POST',{userId:c.id},a);
  let account=(await json('/api/admin/users/'+c.id,'GET',undefined,admin)).user;
  await json('/api/admin/users/'+c.id+'/lock','PUT',{isLocked:true,reason:'Attachment test',version:account.version},admin);
  await denied('Tài khoản bị khóa không tải file dù là thành viên',()=>api(base+'/'+file.id+'/content','GET',undefined,c),401);
  await denied('Tài khoản bị khóa không upload',()=>upload('locked.txt',Buffer.from('locked'),uuid(),c),401);
  account=(await json('/api/admin/users/'+c.id,'GET',undefined,admin)).user;
  await json('/api/admin/users/'+c.id+'/lock','PUT',{isLocked:false,version:account.version},admin);
  return {a,b,c,group,other,photo,file,fileBytes,png,checks,api,json,uuid,check,until,delay,upload};
}
async function uiContext(){
  const [a,b]=await Promise.all(['a','b'].map(user));await delay(1500);
  const group=(await json('/api/groups','POST',{name:'Attachment UI '+Date.now()},a)).id;
  const other=(await json('/api/groups','POST',{name:'Attachment other UI '+Date.now()},a)).id;
  await json('/api/groups/'+group+'/members','POST',{userId:b.id},a);
  return{a,b,group,other,png};
}
async function run(){try{const uiOnly=process.argv.includes('--ui-only');const context=uiOnly?await uiContext():await main();if(uiOnly||process.argv.includes('--ui'))await require('./attachment-ui-test.cjs').run(context);if(!uiOnly)console.log('PASS '+checks+' API checks');}
  finally{if(storageStopped)docker(['compose','start','attachment-storage']);await Promise.all(hubs.map(h=>h.stop().catch(()=>{})));
    execFileSync(process.execPath,[path.join(__dirname,'cleanup-test-data.cjs'),'--apply','--wait','--since='+started],{cwd:root,encoding:'utf8',windowsHide:true,stdio:'inherit',timeout:240000});}}
// Chỉ thoát khi finally đã dọn xong browser/fixture; không chờ socket proxy half-open.
if(require.main===module)run().then(()=>process.exit(0),e=>{console.error(e.message);process.exit(1);});
module.exports={api,json,uuid};
