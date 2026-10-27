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
const port = 4387;
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
    `localStorage.setItem('chatapp.accessToken', ${JSON.stringify(session.accessToken)}); localStorage.setItem('chatapp.expiresAt', ${JSON.stringify(session.expiresAt)});` });
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
async function main() {
  await new Promise(r => server.listen(port, 'localhost', r));
  const run = Date.now().toString(36), password = 'Passw0rd!';
  async function user(suffix) {
    const userName = 'ui' + run + suffix, displayName = 'Presence UI ' + suffix.toUpperCase();
    await request('http://localhost:5000/api/auth/register', 'POST', { userName, displayName, email: userName + '@test.local', password });
    const login = await request('http://localhost:5000/api/auth/login', 'POST', { userName, password });
    const me = await request('http://localhost:5000/api/users/me', 'GET', undefined, login.accessToken);
    return { ...login, ...me };
  }
  const a = await user('a'), b = await user('b');
  await delay(1200);
  const group = await request('http://localhost:5000/api/groups', 'POST', { name: 'Nhóm Presence UI' }, a.accessToken);
  await request('http://localhost:5000/api/groups/' + group.id + '/members', 'POST', { userId: b.id }, a.accessToken);
  await request('http://localhost:5000/api/friends/requests', 'POST', { userId: b.id }, a.accessToken);
  const friend = await request('http://localhost:5000/api/friends/requests/' + a.id + '/accept', 'POST', {}, b.accessToken);
  // Lịch sử cũ độc lập Outbox: dữ liệu fixture trong đúng nhóm do script tạo.
  const fixtureIds = [0, 1].map(() => { const h=crypto.randomBytes(16).toString('hex'); return [h.slice(0,8),h.slice(8,12),h.slice(12,16),h.slice(16,20),h.slice(20)].join('-'); });
  const fixtureScript = `const chat=db.getSiblingDB('chat_history');
    const group=${JSON.stringify(group.id)}, ids=${JSON.stringify(fixtureIds)}, sender=${JSON.stringify(b.id)};
    const start=Date.parse('2026-10-12T00:00:00+07:00');
    const docs=ids.map((id,i)=>({_id:id,groupId:group,senderId:sender,senderName:'Presence UI B',content:'Tin tuần cũ '+(i+1),sequenceNumber:NumberLong(String(i+1)),createdAt:new Date(start+i*1000),createdAtTicks:NumberLong(String(BigInt(start+i*1000)*10000n+621355968000000000n)),weekStartUtc:new Date(start)}));
    chat.messages.insertMany(docs); chat.message_keys.insertMany(docs.map(m=>({_id:m._id,groupId:group,senderId:sender})));
    chat.conversation_state.updateOne({_id:group},{$max:{lastSeq:NumberLong('2')},$setOnInsert:{hasPending:false,leaseUntil:new Date(0),leaseVersion:NumberLong('0')}},{upsert:true});`;
  execFileSync('docker',['exec','-i','mongo-router','mongosh','--quiet','chat_history','--file','/dev/stdin'],{input:fixtureScript,encoding:'utf8',windowsHide:true,stdio:'pipe'});
  await delay(800);
  const ba = await openBrowser(9397, a, '/chat/' + group.id);
  const bb = await openBrowser(9398, b, '/contacts');
  await check('Vào thẳng Danh bạ vẫn kết nối presence', async () => (await bb.text()).includes('Đang hoạt động'));
  await check('Chat hiển thị đầy đủ bạn bè online', async () => (await ba.text()).includes('Presence UI B') && (await ba.text()).includes('Bạn bè đang hoạt động · 1'));
  await check('Nhóm có thành viên khác online dù B đang ở Danh bạ', async () => (await ba.text()).includes('1 thành viên khác đang online'));
  await until(() => ba.evaluate(`!!document.querySelector('#gl-tab-online')`));
  await ba.evaluate(`document.querySelector('#gl-tab-online').click()`);
  await check('Bộ lọc nhóm/cuộc trò chuyện hoạt động', async () => (await ba.evaluate(`document.querySelector('#gl-panel').innerText`)).includes('Nhóm Presence UI'));
  await bb.evaluate(`document.querySelector('#ct-tab-online').click()`);
  await check('Danh bạ có bộ lọc toàn bộ bạn bè online', async () => (await bb.evaluate(`document.querySelector('#ct-panel').innerText`)).includes('Presence UI A'));
  const addedGroup = await request('http://localhost:5000/api/groups', 'POST', { name: 'Nhóm mới từ phía B' }, b.accessToken);
  await request('http://localhost:5000/api/groups/' + addedGroup.id + '/members', 'POST', { userId: a.id }, b.accessToken);
  await check('Được thêm nhóm từ phía khác → danh sách tự cập nhật', async () => (await ba.evaluate(`document.querySelector('#gl-panel')?.innerText || ''`)).includes('Nhóm mới từ phía B'));
  await bb.call('Page.navigate', { url: 'http://localhost:' + port + '/chat/' + group.id });
  await until(() => bb.evaluate(`!!document.querySelector('app-message-input input')`));
  // Mô phỏng gõ liên tục: DOM có thể sẵn sàng trước khi kết nối hub của trang mới hoàn tất.
  // Typing là tín hiệu best effort, một phím duy nhất ngay lúc bắt tay có thể bị bỏ qua hợp lệ.
  await check('Angular hiển thị đang gõ', async () => {
    await bb.evaluate(`{ const e = document.querySelector('app-message-input input'); e.value='Đang nhập'; e.dispatchEvent(new Event('input', {bubbles:true})); }`);
    return (await ba.text()).includes('Presence UI B đang gõ');
  });
  await check('Dừng nhập tự xóa chỉ báo', async () => !(await ba.text()).includes('Presence UI B đang gõ'));
  await bb.evaluate(`document.querySelector('app-message-input form').dispatchEvent(new Event('submit', {bubbles:true,cancelable:true}))`);
  await check('Angular gửi và nhận tin thật', async () => (await ba.text()).includes('Đang nhập'));
  const unread = async () => (await request('http://localhost:5000/api/notifications/unread','GET',undefined,a.accessToken)).find(c=>c.groupId===group.id);
  await until(async()=> (await unread()).unreadCount === 0);
  const readBefore = (await unread()).lastReadSequence;
  await ba.evaluate(`{ const e=document.querySelector('app-message-input input'); e.value='Nháp được giữ khi xem tuần'; e.dispatchEvent(new Event('input',{bubbles:true})); }`);
  await ba.evaluate(`{ const e=document.querySelector('#chat-week-date'); e.value='2026-10-12'; e.dispatchEvent(new Event('input',{bubbles:true})); Array.from(document.querySelectorAll('button')).find(b=>b.textContent.trim()==='Xem tuần').click(); }`);
  await check('Angular chọn tuần lịch sử',async()=> (await ba.text()).includes('Tuần 12/10/2026'));
  await check('Tuần cũ chỉ hiện tin đúng tuần',async()=> {
    const text=await ba.evaluate(`document.querySelector('app-message-list')?.innerText || ''`);
    return text.includes('Tin tuần cũ 1') && !text.includes('Đang nhập');
  });
  await check('Xem tuần cũ có nút về hiện tại và ẩn composer',()=>ba.evaluate(`!document.querySelector('app-message-input') && Array.from(document.querySelectorAll('button')).some(b=>b.textContent.trim()==='Về tin mới nhất')`));
  await bb.evaluate(`{ const e=document.querySelector('app-message-input input'); e.value='Tin mới ngoài tuần đang xem'; e.dispatchEvent(new Event('input',{bubbles:true})); document.querySelector('app-message-input form').dispatchEvent(new Event('submit',{bubbles:true,cancelable:true})); }`);
  await check('Tin ngoài tuần không chen vào lịch sử đang xem',async()=> {
    const text=await ba.evaluate(`document.querySelector('app-message-list')?.innerText || ''`);
    return (await unread()).unreadCount >= 1 && !text.includes('Tin mới ngoài tuần đang xem');
  });
  await delay(800);
  await check('Xem tuần cũ không tự đánh dấu đọc tin mới',async()=> (await unread()).lastReadSequence===readBefore && (await unread()).unreadCount>=1);
  const connectionsBefore = [ba.connections, bb.connections];
  execFileSync('docker',['compose','restart','chat-service-1','chat-service-2'],{cwd:path.resolve(__dirname,'..'),windowsHide:true,stdio:'pipe'});
  // Snapshot cũ có thể còn trong DOM khi lệnh Docker trả về; phải thấy handshake mới của cả hai tab.
  await until(()=>ba.connections > connectionsBefore[0] && bb.connections > connectionsBefore[1],30000);
  await until(()=>ba.evaluate(`!document.querySelector('.ca-banner.reconnecting') && (document.querySelector('.ca-head')?.innerText || '').includes('1 thành viên khác đang online')`),30000);
  await check('Reconnect giữ tuần đang xem',async()=> (await ba.text()).includes('Tuần 12/10/2026') && (await ba.evaluate(`document.querySelector('app-message-list')?.innerText || ''`)).includes('Tin tuần cũ 1'));
  await ba.evaluate(`Array.from(document.querySelectorAll('button')).find(b=>b.textContent.trim()==='Về tin mới nhất').click()`);
  await check('Về tin mới nhất phục hồi composer và lấy tin bị lỡ',async()=> (await ba.evaluate(`document.querySelector('app-message-list')?.innerText || ''`)).includes('Tin mới ngoài tuần đang xem') && await ba.evaluate(`!!document.querySelector('app-message-input input')`));
  await check('Đổi tuần/reconnect giữ bản nháp chưa gửi',()=>ba.evaluate(`document.querySelector('app-message-input input').value==='Nháp được giữ khi xem tuần'`));
  await check('Về luồng hiện tại mới đánh dấu đã đọc',async()=> (await unread()).unreadCount===0);
  try {
    execFileSync('docker', ['compose', 'stop', 'presence-service'], { cwd: path.resolve(__dirname, '..'), windowsHide: true, stdio: 'pipe' });
    await check('Presence lỗi → chưa xác định trạng thái', async () => (await ba.text()).includes('Chưa xác định trạng thái'));
    await bb.evaluate(`{ const e=document.querySelector('app-message-input input'); e.value='Chat vẫn chạy khi Presence tắt'; e.dispatchEvent(new Event('input',{bubbles:true})); document.querySelector('app-message-input form').dispatchEvent(new Event('submit',{bubbles:true,cancelable:true})); }`);
    await check('Presence tắt vẫn chat được', async () => (await ba.text()).includes('Chat vẫn chạy khi Presence tắt'));
  } finally {
    execFileSync('docker', ['compose', 'start', 'presence-service'], { cwd: path.resolve(__dirname, '..'), windowsHide: true, stdio: 'pipe' });
  }
  await check('Presence phục hồi tự cập nhật online', async () => (await ba.text()).includes('1 thành viên khác đang online'));
  await bb.call('Page.navigate', { url: 'http://localhost:' + port + '/chat/' + friend.directGroupId });
  await ba.call('Page.navigate', { url: 'http://localhost:' + port + '/chat/' + friend.directGroupId });
  await check('Chat riêng hiển thị người đối diện online', async () => (await ba.evaluate(`document.querySelector('.ca-head')?.innerText || ''`)).includes('Đang hoạt động'));
  await ba.call('Emulation.setEmulatedMedia', { features: [{ name: 'prefers-color-scheme', value: 'dark' }] });
  await delay(500);
  const output = process.env.PRESENCE_SCREENSHOT;
  if (output) {
    const screenshot = await ba.call('Page.captureScreenshot', { format: 'png' }); fs.writeFileSync(output, Buffer.from(screenshot.data, 'base64'));
  }
  await bb.call('Page.navigate', { url: 'http://localhost:' + port + '/contacts' });
  await check('Danh bạ có bạn trước khi hủy từ phía đối diện', async () => (await bb.evaluate(`document.querySelector('#ct-panel')?.innerText || ''`)).includes('Presence UI A'));
  await request('http://localhost:5000/api/friends/' + b.id, 'DELETE', undefined, a.accessToken);
  await check('Danh bạ tự đồng bộ khi người kia hủy kết bạn', async () => !(await bb.evaluate(`document.querySelector('#ct-panel')?.innerText || ''`)).includes('Presence UI A'));
  if (errors.length) throw new Error('Lỗi runtime: ' + errors.join('\n'));
  console.log('PASS ' + checks + ' kiểm tra UI; không có lỗi JavaScript runtime.');
}
main().catch(async e => {
  console.error(e.stack);
  for (let i=0;i<pages.length;i++) {
    try { console.error('UI diagnostic ' + i + ': ' + await pages[i].text()); } catch {}
  }
  if (errors.length) console.error(errors.join('\n'));
  process.exitCode = 1;
}).finally(async () => {
  for (const ws of clients) ws.terminate();
  for (const socket of sockets) socket.destroy();
  for (const browser of browsers) { try { browser.kill(); } catch {} }
  server.close();
  await delay(800);
  for (const profile of profiles) {
    // Chỉ xóa profile tạm do chính script vừa tạo, đã kiểm tra nằm trong temp.
    const resolved = path.resolve(profile);
    if (resolved.startsWith(path.resolve(os.tmpdir()) + path.sep) && path.basename(resolved).startsWith('chatapp-presence-ui-')) {
      try { fs.rmdirSync(resolved, { recursive: true }); } catch {}
    }
  }
  // CLI test đã thu kết quả và dọn tài nguyên; không chờ socket proxy lỗi còn half-open.
  process.exit(process.exitCode || 0);
});
