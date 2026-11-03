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
const port = 4388;
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
  const profile = fs.mkdtempSync(path.join(os.tmpdir(), 'chatapp-attachment-ui-')); profiles.push(profile);
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
async function check(label, condition) { try { await until(condition); } catch(e) { throw new Error(label + ': ' + e.message); } checks++; console.log('[OK] ' + label); }

module.exports.run = async function(context) {
  const {a,b,group,other,png}=context;
  const downloads=fs.mkdtempSync(path.join(os.tmpdir(),'chatapp-attachment-ui-downloads-'));
  profiles.push(downloads);
  try {
    await new Promise(r=>server.listen(port,'localhost',r));
    const owner=await openBrowser(9410,a,'/chat/'+group),peer=await openBrowser(9411,b,'/chat/'+group);
    await check('UI mở cuộc trò chuyện ở hai trình duyệt',async()=>await owner.evaluate('!!document.querySelector("app-message-input")')&&await peer.evaluate('!!document.querySelector("app-message-input")'));
    async function pick(page,files,image=false){
      await page.evaluate(`(()=>{const d=new DataTransfer();for(const f of ${JSON.stringify(files)}){const bytes=Uint8Array.from(atob(f.data),c=>c.charCodeAt(0));d.items.add(new File([bytes],f.name,{type:f.type||'application/octet-stream'}));}const i=document.querySelectorAll('input[type=file]')[${image?0:1}];i.files=d.files;i.dispatchEvent(new Event('change',{bubbles:true}));})()`);
    }
    await pick(owner,[{name:'Anh-UI.png',data:png.toString('base64')},{name:'Bao-cao-UI.txt',data:Buffer.from('Nội dung tải từ giao diện').toString('base64')}]);
    await check('UI chọn nhiều file, có ảnh xem trước',async()=>await owner.evaluate('document.querySelectorAll(".ca-attachment-draft").length===2 && !!document.querySelector(".ca-attachment-draft img")'));
    await check('UI upload xác nhận xong mới cho gửi',async()=>await owner.evaluate('document.querySelectorAll(".ca-attachment-draft").length===2 && document.querySelector(".ca-attachment-drafts").innerText.split("Sẵn sàng gửi").length===3 && !document.querySelector(".ca-send").disabled'));
    await owner.evaluate('document.querySelector(".ca-send").click()');
    await check('UI người nhận thấy ảnh và file realtime',async()=>await peer.evaluate('Array.from(document.querySelectorAll("app-message-attachment")).some(e=>e.innerText.includes("Bao-cao-UI.txt"))'));
    await check('UI ảnh tải bằng API có JWT',async()=>await peer.evaluate('Array.from(document.querySelectorAll(".ca-image-open img")).some(e=>e.alt==="Anh-UI.png" && e.complete && e.naturalWidth>0)'));
    await peer.evaluate('Array.from(document.querySelectorAll(".ca-image-open")).find(e=>e.getAttribute("aria-label").includes("Anh-UI")).click()');
    await check('UI xem ảnh lớn có nút đóng',async()=>await peer.evaluate('!!document.querySelector(".ca-image-lightbox img")'));
    await peer.call('Input.dispatchKeyEvent',{type:'keyDown',key:'Escape',code:'Escape',windowsVirtualKeyCode:27});
    await check('UI đóng ảnh bằng Escape',async()=>await peer.evaluate('!document.querySelector(".ca-image-lightbox")'));
    await peer.call('Browser.setDownloadBehavior',{behavior:'allow',downloadPath:downloads});
    await peer.evaluate('Array.from(document.querySelectorAll("app-message-attachment")).find(e=>e.innerText.includes("Bao-cao-UI.txt")).querySelector(".ca-link").click()');
    await check('UI tải file đúng nội dung',async()=>{const file=path.join(downloads,'Bao-cao-UI.txt');return fs.existsSync(file)&&fs.readFileSync(file,'utf8')==='Nội dung tải từ giao diện';});
    await owner.call('Page.reload');
    await check('UI F5 lịch sử vẫn có đính kèm',async()=>await owner.evaluate('Array.from(document.querySelectorAll("app-message-attachment")).some(e=>e.innerText.includes("Bao-cao-UI.txt"))'));
    await check('UI danh sách chat có preview gửi file',async()=>await owner.evaluate('document.querySelector(".ca-list").innerText.includes("Đã gửi tệp:")'));
    await pick(owner,[{name:'Draft-UI.txt',data:Buffer.from('draft').toString('base64')}]);
    await check('UI draft upload xong',async()=>await owner.evaluate('!!document.querySelector(".ca-attachment-drafts") && document.querySelector(".ca-attachment-drafts").innerText.includes("Sẵn sàng gửi")'));
    await owner.evaluate(`document.querySelector('a.ca-item[href="/chat/${other}"]').click()`);
    await check('UI đổi nhóm không mang nhầm file sang nhóm khác',async()=>await owner.evaluate('!document.querySelector(".ca-attachment-drafts")'));
    await owner.evaluate(`document.querySelector('a.ca-item[href="/chat/${group}"]').click()`);
    await check('UI trở về nhóm vẫn giữ draft',async()=>await owner.evaluate('!!document.querySelector(".ca-attachment-drafts") && document.querySelector(".ca-attachment-drafts").innerText.includes("Draft-UI.txt")'));
    await owner.evaluate('document.querySelector(".ca-attachment-draft .ca-iconbtn").click()');
    await check('UI bỏ file khỏi bản nháp',async()=>await owner.evaluate('!document.querySelector(".ca-attachment-drafts")'));
    await pick(owner,[{name:'Fake-UI.png',data:Buffer.from('fake').toString('base64')}],true);
    await check('UI lỗi định dạng hiện lỗi và nút thử lại',async()=>await owner.evaluate('!!document.querySelector(".ca-attachment-draft [role=alert]") && document.querySelector(".ca-send").disabled'));
    await owner.evaluate('document.querySelector(".ca-attachment-draft .ca-iconbtn").click()');
    await pick(owner,[{name:'bad.exe',data:Buffer.from('MZ').toString('base64')}]);
    await check('UI chặn loại file không hỗ trợ',async()=>await owner.evaluate('document.body.innerText.includes("Hỗ trợ JPEG") && !document.querySelector(".ca-attachment-drafts")'));
    await owner.evaluate('Array.from(document.querySelectorAll("button")).find(b=>b.innerText.trim()==="Xem tuần").click()');
    await check('UI xem tuần giữ đính kèm và ẩn composer',async()=>await owner.evaluate('!document.querySelector("app-message-input") && Array.from(document.querySelectorAll("app-message-attachment")).some(e=>e.innerText.includes("Bao-cao-UI.txt"))'));
    await peer.evaluate('document.body.classList.remove("dark")');await delay(200);
    const light=await peer.call('Page.captureScreenshot',{format:'png'});fs.writeFileSync(path.join(os.tmpdir(),'chatapp-attachments-light.png'),Buffer.from(light.data,'base64'));
    await peer.evaluate('document.body.classList.add("dark")');await delay(300);
    const dark=await peer.call('Page.captureScreenshot',{format:'png'});fs.writeFileSync(path.join(os.tmpdir(),'chatapp-attachments-dark.png'),Buffer.from(dark.data,'base64'));
    if(errors.length)throw Error(errors.join('\n'));
    console.log('PASS '+checks+' UI checks; light/dark screenshots in TEMP.');
  } catch(error) {
    for(let i=0;i<pages.length;i++){
      try { console.log('UI diagnostic '+i+': '+await pages[i].evaluate('JSON.stringify(Array.from(document.querySelectorAll(".ca-image-open")).map(e=>({text:e.innerText,img:e.querySelector("img")?{complete:e.querySelector("img").complete,width:e.querySelector("img").naturalWidth,src:e.querySelector("img").src}:null,rect:e.getBoundingClientRect().toJSON()})))'));
        const shot=await pages[i].call('Page.captureScreenshot',{format:'png'});fs.writeFileSync(path.join(os.tmpdir(),'chatapp-attachments-failed-'+i+'.png'),Buffer.from(shot.data,'base64')); } catch{}
    }
    throw error;
  } finally {
    for(const ws of clients)ws.terminate();
    for(const socket of sockets)socket.destroy();
    for(const browser of browsers){try{browser.kill();}catch{}}
    server.close();await delay(700);
    for(const profile of profiles){const resolved=path.resolve(profile);if(resolved.startsWith(path.resolve(os.tmpdir())+path.sep)&&path.basename(resolved).startsWith('chatapp-attachment-ui-')){try{fs.rmdirSync(resolved,{recursive:true});}catch{}}}
  }
};
