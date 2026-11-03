// Actual web ports 4200/4201, Edge profile dedicated to this suite, Node 14.
const fs=require('fs'),path=require('path'),http=require('http');
const {spawn,execFileSync}=require('child_process');
const WebSocket=require('../frontend/chat-app/node_modules/ws');
const {api,request,until,delay,check}=require('./backup-system-test.cjs');
const root=path.resolve(__dirname,'..'),runtime=path.join(root,'infra/backups/runtime');
const browsers=[],clients=[],profiles=[],errors=[];
let admin,oldSettings;
function jsonGet(url){return new Promise((resolve,reject)=>{http.get(url,res=>{let body='';res.on('data',x=>body+=x);res.on('end',()=>{try{resolve(JSON.parse(body));}catch(e){reject(e);}});}).on('error',reject);});}
async function open(port,debugPort){
  const profile=fs.mkdtempSync(path.join(runtime,'backup-ui-profile-'));profiles.push(profile);
  const browser=spawn(process.env.EDGE_PATH||'C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe',
    ['--headless=new','--disable-gpu','--no-first-run','--disable-background-networking','--remote-debugging-port='+debugPort,'--user-data-dir='+profile,'about:blank'],{windowsHide:true,stdio:'ignore'});browsers.push(browser);
  let targets;await until(async()=>{try{targets=await jsonGet('http://127.0.0.1:'+debugPort+'/json/list');return targets.some(t=>t.type==='page');}catch{return false;}},20000);
  const ws=new WebSocket(targets.find(t=>t.type==='page').webSocketDebuggerUrl);clients.push(ws);ws.on('error',()=>{});
  await new Promise((r,j)=>{ws.once('open',r);ws.once('error',j);});
  let sequence=0;const waiting=new Map();
  ws.on('message',raw=>{const message=JSON.parse(raw);if(message.id&&waiting.has(message.id)){const p=waiting.get(message.id);waiting.delete(message.id);message.error?p.reject(Error(JSON.stringify(message.error))):p.resolve(message.result);}if(message.method==='Runtime.exceptionThrown')errors.push(message.params.exceptionDetails.text);});
  function call(method,params={}){return new Promise((resolve,reject)=>{const id=++sequence;waiting.set(id,{resolve,reject});ws.send(JSON.stringify({id,method,params}));});}
  async function evaluate(expression){const result=await call('Runtime.evaluate',{expression,awaitPromise:true,returnByValue:true});if(result.exceptionDetails)throw Error(JSON.stringify(result.exceptionDetails));return result.result.value;}
  await call('Runtime.enable');await call('Page.enable');await call('Emulation.setDeviceMetricsOverride',{width:1440,height:1000,deviceScaleFactor:1,mobile:false});
  await call('Page.navigate',{url:'http://localhost:'+port+'/auth/login'});
  return {call,evaluate,text:()=>evaluate('document.body.innerText'),navigate:route=>call('Page.navigate',{url:'http://localhost:'+port+route})};
}
async function click(page,label,scope='document'){await page.evaluate(`Array.from(${scope}.querySelectorAll('button')).find(b=>b.textContent.trim()===${JSON.stringify(label)}).click()`);}
async function login(page,userName,password){
  await until(()=>page.evaluate(`!!document.querySelector('#u')`));
  await page.evaluate(`{for(const [id,value] of ${JSON.stringify([['u',userName],['p',password]])}){const input=document.getElementById(id);input.value=value;input.dispatchEvent(new Event('input',{bubbles:true}));}document.querySelector('.auth-form').dispatchEvent(new Event('submit',{bubbles:true,cancelable:true}));}`);
  await until(()=>page.evaluate(`!document.querySelector('#u')`),20000);
}
async function overview(){return api('/api/admin/backups','GET',undefined,admin);}
async function main(){
  fs.mkdirSync(runtime,{recursive:true});
  const context=JSON.parse(fs.readFileSync(path.join(root,'infra/backups/system/demo-context.local.json'),'utf8'));
  const secrets=JSON.parse(fs.readFileSync(path.join(process.env.APPDATA,'Microsoft/UserSecrets/chatapp-dev/secrets.json'),'utf8').replace(/^\uFEFF/,''));
  admin=await api('/api/auth/login','POST',{userName:'admin',password:secrets['AdminSeed:Password']});oldSettings=(await overview()).settings;
  const source=await open(4200,9491);await login(source,'admin',secrets['AdminSeed:Password']);
  await until(()=>source.evaluate(`!!document.querySelector('a[href="/admin/backups"]')`));await source.evaluate(`document.querySelector('a[href="/admin/backups"]').click()`);
  await until(()=>source.evaluate(`!!document.querySelector('[data-testid="create-backup"]')`));
  await until(async()=>(await source.text()).includes('Lịch tự động'));
  check('Admin reaches backup page through actual login/navigation',(await source.text()).includes('Lịch tự động'));
  await source.evaluate(`{const e=document.querySelector('select[aria-label="Lịch sao lưu"]');e.value='hourly';e.dispatchEvent(new Event('change',{bubbles:true}));const n=document.querySelector('input[aria-label="Số bản giữ lại"]');n.value='1';n.dispatchEvent(new Event('input',{bubbles:true}));}`);
  await click(source,'Lưu lịch');await until(async()=>(await overview()).settings.retentionCount===1);
  check('Schedule and retention save from web',(await overview()).settings.period==='hourly');
  const before=new Set((await overview()).items.map(x=>x.id));
  await source.evaluate(`document.querySelector('[data-testid="create-backup"]').click()`);
  let created;
  await until(async()=>{const data=await overview();created=data.items.find(x=>!before.has(x.id));if(created&&created.status==='failed')throw Error(created.message);return created&&created.status==='complete';},1200000);
  await until(()=>source.evaluate(`!!document.querySelector('[data-backup-id="${created.id}"]') && !document.querySelector('[data-testid="create-backup"]').disabled`));
  check('Create button completes a real backup',created.status==='complete');
  await until(async()=>(await overview()).items.filter(x=>x.status==='complete').length===2,120000);
  const retained=(await overview()).items.filter(x=>x.status==='complete').map(x=>x.id);
  check('Retention keeps newest backup and active recovery backup',retained.length===2&&retained.includes(created.id)&&retained.includes(context.backupId));
  const row=`document.querySelector('[data-backup-id="${created.id}"]')`;
  await click(source,'Kiểm tra',row);await until(async()=>(await source.text()).includes('khớp checksum'));
  check('Checksum button verifies on web',!(await source.evaluate(`document.querySelector('.feedback.error')?.textContent`)));
  const catalog=path.join(root,'infra/backups/system',created.id,'mongo/catalog.json'),original=fs.readFileSync(catalog);
  try{
    fs.appendFileSync(catalog,'CORRUPTION TEST');await click(source,'Kiểm tra',row);
    await until(()=>source.evaluate(`(document.querySelector('.feedback.error')?.textContent||'').includes('Checksum')`));
    check('Damaged backup is reported as an error on web',true);
  }finally{fs.writeFileSync(catalog,original);}
  const bundle=path.join(root,'infra/backups/system',context.backupId),manifest=JSON.parse(fs.readFileSync(path.join(bundle,'manifest.json'),'utf8'));
  const object=path.join(bundle,manifest.files.find(f=>f.path.startsWith('mongo/objects/')).path),temporary=object+'.test-missing';
  try{fs.renameSync(object,temporary);let rejected=false;try{await api('/api/admin/backups/'+context.backupId+'/verify','POST',{},admin);}catch(e){rejected=e.status===400;}check('Missing attachment backup file is rejected',rejected);}
  finally{if(fs.existsSync(temporary))fs.renameSync(temporary,object);}
  check('Original backup verifies after fault test',(await api('/api/admin/backups/'+context.backupId+'/verify','POST',{},admin)).verified);
  await click(source,'Phục hồi riêng',row);await until(()=>source.evaluate(`!!document.querySelector('app-confirm-dialog')`));await click(source,'Hủy','document.querySelector("app-confirm-dialog")');
  check('Restore confirmation can be cancelled',!await source.evaluate(`!!document.querySelector('app-confirm-dialog')`));
  await click(source,'Dọn môi trường phục hồi');await until(async()=>(await overview()).recovery.status==='none',180000);
  await until(()=>source.evaluate(`!document.querySelector('[data-testid="recovery-status"]')`));
  const oldRow=`document.querySelector('[data-backup-id="${context.backupId}"]')`;
  await click(source,'Phục hồi riêng',oldRow);await until(()=>source.evaluate(`!!document.querySelector('app-confirm-dialog')`));await click(source,'Bắt đầu phục hồi','document.querySelector("app-confirm-dialog")');
  await until(async()=>{const state=(await overview()).recovery;if(state.status==='failed')throw Error(state.message);return state.status==='ready';},1200000);
  await until(()=>source.evaluate(`!!document.querySelector('[data-testid="open-recovery"]')`));
  check('Restore button builds a separate web',(await overview()).recovery.url==='http://localhost:4201');
  const sourceShot=await source.call('Page.captureScreenshot',{format:'png'});fs.writeFileSync(path.join(runtime,'backup-source.png'),Buffer.from(sourceShot.data,'base64'));
  const recovered=await open(4201,9492);await login(recovered,context.fixtureUser,context.fixturePassword);await recovered.navigate('/chat/'+context.groupId);
  await until(async()=>(await recovered.text()).includes('A - saved before the full system backup'),30000);
  check('Recovered messages are visible in actual chat web',(await recovered.text()).includes('backup-demo.txt'));
  check('Recovery banner stays visible',(await recovered.text()).includes('BẢN PHỤC HỒI RIÊNG'));
  check('Recovery banner leaves message input usable',await recovered.evaluate(`{const input=document.querySelector('app-message-input input[type="text"]');const banner=document.querySelector('.ca-recovery-banner');!!input && !!banner && input.getBoundingClientRect().bottom<=innerHeight && input.getBoundingClientRect().top>=banner.getBoundingClientRect().bottom;}`));
  check('Post-backup message is absent from recovered web',!(await recovered.text()).includes('AFTER BACKUP - must not appear'));
  const downloadDir=path.join(runtime,'backup-downloads');fs.mkdirSync(downloadDir,{recursive:true});
  const oldFile=path.join(downloadDir,'backup-demo.txt');if(fs.existsSync(oldFile))fs.unlinkSync(oldFile);
  await recovered.call('Page.setDownloadBehavior',{behavior:'allow',downloadPath:downloadDir});
  const attachment=`Array.from(document.querySelectorAll('app-message-attachment')).find(e=>e.textContent.includes('backup-demo.txt'))`;
  await until(()=>recovered.evaluate(`!!(${attachment})?.querySelector('.ca-file-actions button')`));
  await recovered.evaluate(`(${attachment}).querySelector('.ca-file-actions button').click()`);
  await until(()=>fs.existsSync(oldFile),20000);
  check('User downloads exact restored file from web',fs.readFileSync(oldFile).equals(Buffer.from('Chapter 7 - backup object bytes\nUTF-8: Xin chào')));
  const recoveryShot=await recovered.call('Page.captureScreenshot',{format:'png'});fs.writeFileSync(path.join(runtime,'backup-recovery.png'),Buffer.from(recoveryShot.data,'base64'));
  await recovered.call('Emulation.setEmulatedMedia',{features:[{name:'prefers-color-scheme',value:'dark'}]});
  check('No uncaught browser JavaScript errors',errors.length===0);
  fs.writeFileSync(path.join(runtime,'backup-web-test-report.json'),JSON.stringify({passed:true,createdBackup:created.id,recoveryBackup:context.backupId,screenshots:['backup-source.png','backup-recovery.png']},null,2));
  console.log('PASS backup web UI, download, corruption, retention and restore checks.');
}
main().catch(e=>{console.error(e.stack);process.exitCode=1;}).finally(async()=>{
  if(admin&&oldSettings)try{await api('/api/admin/backups/settings','PUT',oldSettings,admin);}catch(e){console.error('Restore schedule: '+e.message);process.exitCode=1;}
  for(const client of clients)client.close();for(const browser of browsers)browser.kill();await delay(1000);
  for(const profile of profiles){const resolved=path.resolve(profile);if(!resolved.startsWith(runtime+path.sep+'backup-ui-profile-'))continue;try{fs.rmdirSync(resolved,{recursive:true});}catch{}}
});
