// Node 14; dùng ws đã có trong Angular. Chạy sau ng build và khởi động backend.
// Edge headless dùng profile tạm riêng, không truy cập phiên trình duyệt của người dùng.
const http = require('http');
const fs = require('fs');
const path = require('path');
const os = require('os');
const crypto = require('crypto');
const { spawn, execFileSync } = require('child_process');
const WebSocket = require('../frontend/chat-app/node_modules/ws');
const root = path.resolve(__dirname, '../frontend/chat-app/dist/chat-app');
const profiles = [], browsers = [], clients = [], errors = [];
const pages = [], sockets = new Set();
const delay = ms => new Promise(r => setTimeout(r, ms));
const port = 4389;
let checks = 0;
async function request(url, method = 'GET', body, token) {
  return new Promise((resolve, reject) => {
    const data = body === undefined ? null : JSON.stringify(body);
    const req = http.request(url, { method, headers: {
      ...(data ? { 'Content-Type': 'application/json', 'Content-Length': Buffer.byteLength(data) } : {}),
      ...(token ? { Authorization: 'Bearer ' + token } : {})
    } }, res => {
      let text = ''; res.on('data', d => text += d); res.on('end', () => {
        if (res.statusCode >= 400) reject(new Error(method + ' ' + url + ': ' + res.statusCode + ' ' + text));
        else { try { resolve(text ? JSON.parse(text) : null); } catch { resolve(text); } }
      });
    });
    req.on('error', reject); if (data) req.write(data); req.end();
  });
}
const server = http.createServer((req, res) => {
  if (req.url.startsWith('/api/') || req.url.startsWith('/hubs/')) {
    const p = http.request({ hostname: 'localhost', port: 5000, path: req.url, method: req.method, headers: req.headers }, up => {
      res.writeHead(up.statusCode, up.headers); up.pipe(res);
    });
    p.on('error', () => { res.writeHead(502); res.end(); }); req.pipe(p); return;
  }
  const requested = path.resolve(root, '.' + decodeURIComponent(req.url.split('?')[0]));
  if (!requested.startsWith(root + path.sep) && requested !== root) { res.writeHead(403); res.end(); return; }
  const file = fs.existsSync(requested) && fs.statSync(requested).isFile() ? requested : path.join(root, 'index.html');
  res.setHeader('Content-Type', ({ '.js': 'application/javascript', '.css': 'text/css', '.html': 'text/html', '.svg': 'image/svg+xml' })[path.extname(file)] || 'application/octet-stream');
  fs.createReadStream(file).pipe(res);
});
server.on('connection', socket => {
  sockets.add(socket); socket.on('close', () => sockets.delete(socket)); socket.on('error', () => {});
});
server.on('clientError', (_error, socket) => socket.destroy());
server.on('upgrade', (req, socket, head) => {
  const p = http.request({ hostname: 'localhost', port: 5000, path: req.url, headers: req.headers });
  p.on('upgrade', (up, remote, remoteHead) => {
    socket.write('HTTP/1.1 101 Switching Protocols\r\n' + Object.entries(up.headers).map(([k, v]) => k + ': ' + v).join('\r\n') + '\r\n\r\n');
    if (remoteHead.length) socket.write(remoteHead); if (head.length) remote.write(head);
    socket.pipe(remote).pipe(socket); remote.on('error', () => socket.destroy()); socket.on('error', () => remote.destroy());
    socket.on('close', () => remote.destroy()); remote.on('close', () => socket.destroy());
  });
  p.on('response', () => socket.destroy()); p.on('error', () => socket.destroy()); p.end();
});
async function openBrowser(debugPort, session, route) {
  const profile = fs.mkdtempSync(path.join(os.tmpdir(), 'chatapp-presence-ui-')); profiles.push(profile);
  const edge = process.env.EDGE_PATH || 'C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe';
  const browser = spawn(edge, ['--headless=new', '--disable-gpu', '--no-first-run', '--disable-background-networking',
    '--remote-debugging-port=' + debugPort, '--user-data-dir=' + profile, 'about:blank'], { windowsHide: true, stdio: 'ignore' });
  browsers.push(browser);
  let targets;
  await until(async () => { try { targets = await request('http://localhost:' + debugPort + '/json/list'); return targets.length; } catch { return false; } }, 15000);
  const ws = new WebSocket(targets.find(t => t.type === 'page').webSocketDebuggerUrl); clients.push(ws);
  ws.on('error', () => {});
  await new Promise((r, j) => { ws.once('open', r); ws.once('error', j); });
  let seq = 0, connections = 0; const waiting = new Map();
  ws.on('message', data => {
    const m = JSON.parse(data);
    if (m.id && waiting.has(m.id)) { const p = waiting.get(m.id); waiting.delete(m.id); m.error ? p.reject(new Error(JSON.stringify(m.error))) : p.resolve(m.result); }
    if (m.method === 'Runtime.exceptionThrown') errors.push(m.params.exceptionDetails.text + ' ' + (m.params.exceptionDetails.exception || {}).description);
    if (m.method === 'Network.webSocketHandshakeResponseReceived' && m.params.response.status === 101) connections++;
  });
  function call(method, params = {}) { return new Promise((resolve, reject) => {
    const id = ++seq; waiting.set(id, { resolve, reject }); ws.send(JSON.stringify({ id, method, params }));
  }); }
  async function evaluate(expression) {
    const result = await call('Runtime.evaluate', { expression, awaitPromise: true, returnByValue: true });
    if (result.exceptionDetails) throw new Error(JSON.stringify(result.exceptionDetails));
    return result.result.value;
  }
  await call('Runtime.enable'); await call('Page.enable'); await call('Network.enable');
  await call('Emulation.setDeviceMetricsOverride', { width: 1365, height: 900, deviceScaleFactor: 1, mobile: false });
  await call('Page.addScriptToEvaluateOnNewDocument', { source:
    `if (!sessionStorage.getItem('chatapp.ui.seeded')) { localStorage.setItem('chatapp.accessToken', ${JSON.stringify(session.accessToken)}); localStorage.setItem('chatapp.expiresAt', ${JSON.stringify(session.expiresAt)}); sessionStorage.setItem('chatapp.ui.seeded','1'); }` });
  await call('Page.navigate', { url: 'http://localhost:' + port + route });
  const page = { call, evaluate, text: () => evaluate('document.body.innerText'), get connections() { return connections; } };
  pages.push(page);
  return page;
}
async function until(condition, timeout = 12000) {
  const start = Date.now(); while (Date.now() - start < timeout) { if (await condition()) return; await delay(200); }
  throw new Error('Quá thời gian chờ điều kiện UI');
}
async function check(label, condition) { await until(condition); checks++; console.log('[OK] ' + label); }

let cleanupTarget, cleanupAdmin;
async function main() {
  await new Promise(r=>server.listen(port,'localhost',r));
  const credentials=Object.fromEntries(fs.readFileSync(path.resolve(__dirname,'../infra/admin-seed-credentials.local.txt'),'utf8').trim().split(/\r?\n/).map(line=>{const i=line.indexOf('=');return[line.slice(0,i),line.slice(i+1)];}));
  const api=(url,method='GET',body,user)=>request('http://localhost:5000'+url,method,body,user?.accessToken);
  const admin=await api('/api/auth/login','POST',{userName:credentials.UserName,password:credentials.Password});
  cleanupAdmin=admin;
  const run=Date.now().toString(36),password='Passw0rd!';
  async function user(suffix){const userName='au'+run+suffix;await api('/api/auth/register','POST',{userName,displayName:'UI quản trị '+suffix,email:userName+'@test.local',password});const login=await api('/api/auth/login','POST',{userName,password});return{...login,...await api('/api/users/me','GET',undefined,login),password};}
  const owner=await user('Owner'), manager=await user('Admin'), member=await user('Member'), target=await user('Target'), extra=await user('New');
  cleanupTarget=target;
  const group=await api('/api/groups','POST',{name:'Nhóm UI quản trị '+run,description:'Kiểm thử vai trò và phê duyệt'},owner);
  await api('/api/groups/'+group.id+'/members','POST',{userId:manager.id},owner);
  await api('/api/groups/'+group.id+'/members','POST',{userId:member.id},owner);
  const initial=await api('/api/groups/'+group.id,'GET',undefined,owner);
  await api('/api/groups/'+group.id+'/members/'+manager.id+'/role','PUT',{role:'Admin',version:initial.group.version},owner);
  const bo=await openBrowser(9400,owner,'/chat/'+group.id);
  await until(()=>bo.evaluate(`!!document.querySelector('button[aria-label="Thành viên nhóm"]')`));
  await bo.evaluate(`document.querySelector('button[aria-label="Thành viên nhóm"]').click()`);
  await check('Panel hiển thị trưởng nhóm và quản trị viên',async()=>{const t=await bo.evaluate(`document.querySelector('app-member-panel')?.innerText || ''`);return t.includes('Trưởng nhóm')&&t.includes('Quản trị viên');});
  async function click(page,text){await page.evaluate(`Array.from(document.querySelectorAll('button')).find(b=>b.textContent.trim()===${JSON.stringify(text)}).click()`);}
  async function confirm(page){await until(()=>page.evaluate(`!!document.querySelector('app-confirm-dialog')`));await page.evaluate(`Array.from(document.querySelectorAll('app-confirm-dialog button')).find(b=>b.textContent.trim()==='Xác nhận').click()`);await until(()=>page.evaluate(`!document.querySelector('app-confirm-dialog')`));}
  await click(bo,'Bật phê duyệt');await confirm(bo);
  await check('Owner bật phê duyệt trên giao diện',async()=> (await bo.text()).includes('Phê duyệt thành viên: Đang bật'));
  await click(bo,'Tạo liên kết mời mới');await confirm(bo);
  await until(()=>bo.evaluate(`!!document.querySelector('#group-invite-link')?.value`));
  const inviteUrl=await bo.evaluate(`document.querySelector('#group-invite-link').value`),token=inviteUrl.split('/').pop();
  await check('Giao diện tạo link mời hợp lệ',async()=>/^[a-f0-9]{48}$/.test(token));
  const bt=await openBrowser(9401,target,'/groups/join/'+token);
  await check('Link hiện thông tin nhóm và yêu cầu tham gia',async()=> (await bt.text()).includes(group.name)&&(await bt.text()).includes('Yêu cầu tham gia'));
  await click(bt,'Yêu cầu tham gia');
  await check('Người xin vào thấy trạng thái chờ',async()=> (await bt.text()).includes('đang chờ phê duyệt'));
  await check('Người chờ không có ô nhập tin',()=>bt.evaluate(`!document.querySelector('app-message-input')`));
  await check('Owner tự cập nhật danh sách chờ duyệt',async()=> (await bo.evaluate(`document.querySelector('app-member-panel')?.innerText || ''`)).includes(target.displayName));
  await bo.evaluate(`Array.from(document.querySelectorAll('.mp-request')).find(r=>r.textContent.includes(${JSON.stringify(target.displayName)})).querySelector('button').click()`);
  await check('Duyệt xong thành viên xuất hiện',async()=> (await bo.evaluate(`document.querySelector('.mp-list')?.innerText || ''`)).includes(target.displayName));
  await check('Người được duyệt có nút mở cuộc trò chuyện',()=>bt.evaluate(`Array.from(document.querySelectorAll('a')).some(a=>a.textContent.trim()==='Mở cuộc trò chuyện')`));
  await bt.evaluate(`Array.from(document.querySelectorAll('a')).find(a=>a.textContent.trim()==='Mở cuộc trò chuyện').click()`);
  await until(()=>bt.evaluate(`!!document.querySelector('app-message-input input')`));
  await bt.evaluate(`{const e=document.querySelector('app-message-input input');e.value='Lịch sử UI trước khi khóa';e.dispatchEvent(new Event('input',{bubbles:true}));document.querySelector('app-message-input form').dispatchEvent(new Event('submit',{bubbles:true,cancelable:true}));}`);
  await check('Người vừa được duyệt gửi tin được',async()=> (await bo.text()).includes('Lịch sử UI trước khi khóa'));
  async function memberAction(value){await bo.evaluate(`{const e=document.querySelector('select[aria-label="Quản lý ${member.displayName}"]');e.value=${JSON.stringify(value)};e.dispatchEvent(new Event('change',{bubbles:true}));}`);await confirm(bo);}
  await memberAction('promote');
  await check('Bổ nhiệm Admin cập nhật vai trò',async()=>{const rows=await bo.evaluate(`Array.from(document.querySelectorAll('.mp-list .mp-row')).map(r=>r.innerText)`);return rows.some(t=>t.includes(member.displayName)&&t.includes('Quản trị viên'));});
  await memberAction('demote');
  await check('Miễn nhiệm Admin cập nhật vai trò',async()=>{const rows=await bo.evaluate(`Array.from(document.querySelectorAll('.mp-list .mp-row')).map(r=>r.innerText)`);return rows.some(t=>t.includes(member.displayName)&&!t.includes('Quản trị viên'));});
  await bo.evaluate(`{const e=document.querySelector('select[aria-label="Quản lý ${manager.displayName}"]');e.value='transfer';e.dispatchEvent(new Event('change',{bubbles:true}));}`);await confirm(bo);
  await check('Chuyển trưởng nhóm, Owner cũ mất nút cấu hình',async()=>{const text=await bo.evaluate(`document.querySelector('app-member-panel')?.innerText || ''`);return !text.includes('Tạo liên kết mời mới')&&!text.includes('Tắt phê duyệt')&&text.includes('Rời nhóm');});
  await click(bo,'Thêm');
  await bo.evaluate(`{const e=document.querySelector('#mp-q');e.value=${JSON.stringify(extra.userName)};e.dispatchEvent(new Event('input',{bubbles:true}));}`);
  await until(async()=> (await bo.evaluate(`document.querySelector('.mp-results')?.innerText || ''`)).includes(extra.displayName));
  await bo.evaluate(`Array.from(document.querySelectorAll('.mp-results button')).find(b=>b.textContent.includes(${JSON.stringify(extra.displayName)})).click()`);
  await check('Member thêm người trên UI tạo yêu cầu chờ',async()=> (await bo.text()).includes('Đã gửi yêu cầu, chờ trưởng nhóm hoặc quản trị viên duyệt.'));
  await check('Người được đề xuất chưa hiện trong thành viên',async()=> !(await bo.evaluate(`document.querySelector('.mp-list')?.innerText || ''`)).includes(extra.displayName));
  const ba=await openBrowser(9399,admin,'/admin/users');
  await check('SystemAdmin mở trang quản trị người dùng',async()=> (await ba.text()).includes('Quản lý ứng dụng')&&await ba.evaluate(`!!document.querySelector('#admin-user-search')`));
  await check('Trang admin có phân trang',async()=> (await ba.text()).includes('Trang sau')&&(await ba.text()).includes('Trang trước'));
  async function searchTarget(){await ba.evaluate(`{const e=document.querySelector('#admin-user-search');e.value=${JSON.stringify(target.email)};e.dispatchEvent(new Event('input',{bubbles:true}));document.querySelector('app-admin-users form').dispatchEvent(new Event('submit',{bubbles:true,cancelable:true}));}`);await until(async()=> (await ba.evaluate(`document.querySelector('tbody')?.innerText || ''`)).includes(target.email));}
  await searchTarget();
  await check('Tìm email lọc đúng tài khoản',()=>ba.evaluate(`document.querySelectorAll('tbody tr').length===1`));
  await click(ba,'Chi tiết');
  await check('Chi tiết có thông tin tài khoản',async()=> (await ba.evaluate(`document.querySelector('.admin-detail')?.innerText || ''`)).includes(target.id));
  await click(ba,'Khóa');
  await ba.evaluate(`{const e=document.querySelector('#lock-reason');e.value='Kiểm thử khóa qua giao diện';e.dispatchEvent(new Event('input',{bubbles:true}));}`);
  await click(ba,'Xác nhận');
  await check('Khóa qua UI cập nhật trạng thái',async()=> (await ba.evaluate(`document.querySelector('tbody')?.innerText || ''`)).includes('Đã khóa'));
  await check('User đang online bị đưa về đăng nhập',()=>bt.evaluate(`location.pathname==='/auth/login'`));
  await click(ba,'Mở khóa');await click(ba,'Xác nhận');
  await check('Mở khóa qua UI cập nhật trạng thái',async()=> (await ba.evaluate(`document.querySelector('tbody')?.innerText || ''`)).includes('Đang hoạt động'));
  await bt.evaluate(`{for(const [name,value] of Object.entries(${JSON.stringify({userName:target.userName,password})})){const e=document.querySelector('input[formcontrolname="'+name+'"]');e.value=value;e.dispatchEvent(new Event('input',{bubbles:true}));}document.querySelector('app-login form').dispatchEvent(new Event('submit',{bubbles:true,cancelable:true}));}`);
  await until(()=>bt.evaluate(`location.pathname==='/chat'`));
  await bt.call('Page.navigate',{url:'http://localhost:'+port+'/chat/'+group.id});
  await check('Đăng nhập lại giữ lịch sử cũ',async()=> (await bt.text()).includes('Lịch sử UI trước khi khóa'));
  await bt.call('Page.navigate',{url:'http://localhost:'+port+'/admin/users'});
  await check('Guard đưa user thường khỏi admin',()=>bt.evaluate(`location.pathname==='/chat' && !document.querySelector('app-admin-users')`));
  await ba.evaluate(`Array.from(document.querySelectorAll('.admin-tabs a')).find(a=>a.textContent.trim()==='Nhóm').click()`);
  await until(()=>ba.evaluate(`!!document.querySelector('app-admin-groups form')`));
  await ba.evaluate(`{const e=document.querySelector('app-admin-groups input[type="search"]');e.value=${JSON.stringify(group.name)};e.dispatchEvent(new Event('input',{bubbles:true}));document.querySelector('app-admin-groups form').dispatchEvent(new Event('submit',{bubbles:true,cancelable:true}));}`);
  await check('Admin tìm nhóm và thấy trưởng nhóm mới',async()=> (await ba.evaluate(`document.querySelector('tbody')?.innerText || ''`)).includes(manager.displayName));
  await click(ba,'Xem thành viên');
  await check('Admin xem được thành viên và vai trò',async()=>{const t=await ba.evaluate(`document.querySelector('.admin-detail')?.innerText || ''`);return t.includes(owner.displayName)&&t.includes(manager.displayName)&&t.includes('Trưởng nhóm');});
  await bt.evaluate(`localStorage.removeItem('chatapp.accessToken');localStorage.removeItem('chatapp.expiresAt');`);
  await bt.call('Page.navigate',{url:'http://localhost:'+port+'/groups/join/'+token});
  await check('Link mời khi chưa đăng nhập yêu cầu đăng nhập',async()=> (await bt.text()).includes('Đăng nhập để xem lời mời'));
  await bt.evaluate(`Array.from(document.querySelectorAll('a')).find(a=>a.textContent.trim()==='Đăng nhập').click()`);
  await check('Link mời giữ đường quay lại trang tham gia',()=>bt.evaluate(`location.pathname==='/auth/login'&&new URLSearchParams(location.search).get('returnUrl')==='/groups/join/${token}'`));
  await bt.evaluate(`{for(const [name,value] of Object.entries(${JSON.stringify({userName:target.userName,password})})){const e=document.querySelector('input[formcontrolname="'+name+'"]');e.value=value;e.dispatchEvent(new Event('input',{bubbles:true}));}document.querySelector('app-login form').dispatchEvent(new Event('submit',{bubbles:true,cancelable:true}));}`);
  await check('Đăng nhập xong quay đúng link mời',()=>bt.evaluate(`location.pathname==='/groups/join/${token}'&&Array.from(document.querySelectorAll('a')).some(a=>a.textContent.trim()==='Mở cuộc trò chuyện')`));
  const output=process.env.ADMIN_SCREENSHOT;
  await ba.call('Emulation.setEmulatedMedia',{features:[{name:'prefers-color-scheme',value:'light'}]});await delay(300);
  if(output){const shot=await ba.call('Page.captureScreenshot',{format:'png'});fs.writeFileSync(output,Buffer.from(shot.data,'base64'));}
  await ba.call('Emulation.setEmulatedMedia',{features:[{name:'prefers-color-scheme',value:'dark'}]});
  await check('Trang admin không tràn ngang viewport',()=>ba.evaluate(`document.documentElement.scrollWidth<=innerWidth`));
  if(output){await delay(300);const shot=await ba.call('Page.captureScreenshot',{format:'png'});fs.writeFileSync(output.replace(/\.png$/,'-dark.png'),Buffer.from(shot.data,'base64'));}
  if(errors.length)throw new Error('Lỗi runtime: '+errors.join('\n'));
  console.log('PASS '+checks+' kiểm tra UI quản trị và nhóm.');
}
main().catch(async e=>{console.error(e.stack);for(let i=0;i<pages.length;i++){try{console.error('UI diagnostic '+i+': '+await pages[i].text());}catch{}}process.exitCode=1;}).finally(async()=>{
  if(cleanupTarget&&cleanupAdmin){try{const url='http://localhost:5000/api/admin/users/'+cleanupTarget.id;const current=(await request(url,'GET',undefined,cleanupAdmin.accessToken)).user;if(current.isLocked)await request(url+'/lock','PUT',{isLocked:false,version:current.version,reason:'Dọn fixture UI'},cleanupAdmin.accessToken);}catch(e){console.error('Không dọn được khóa fixture: '+e.message);process.exitCode=1;}}
  for(const ws of clients)ws.terminate();for(const socket of sockets)socket.destroy();for(const browser of browsers){try{browser.kill();}catch{}}server.close();
  await delay(800);
  for(const profile of profiles){const resolved=path.resolve(profile);if(resolved.startsWith(path.resolve(os.tmpdir())+path.sep)&&path.basename(resolved).startsWith('chatapp-presence-ui-')){try{fs.rmdirSync(resolved,{recursive:true});}catch{}}}
  process.exit(process.exitCode||0);
});
