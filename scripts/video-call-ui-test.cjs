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
const port = 4390;
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
  const profile = fs.mkdtempSync(path.join(os.tmpdir(), 'chatapp-video-ui-')); profiles.push(profile);
  const edge = process.env.EDGE_PATH || 'C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe';
  const browser = spawn(edge, ['--headless=new', '--use-fake-device-for-media-stream', '--use-fake-ui-for-media-stream', '--autoplay-policy=no-user-gesture-required', '--disable-gpu', '--no-first-run', '--disable-background-networking',
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
  await call('Page.addScriptToEvaluateOnNewDocument', {source: `window.__videoPeers=[];window.__localStreams=[];const Native=window.RTCPeerConnection;window.RTCPeerConnection=class extends Native{constructor(config){super({...config,iceServers:${process.env.VIDEO_TEST_RELAY === '1' ? 'config.iceServers.filter(s=>s.urls.some(u=>u.startsWith("turn:")||u.startsWith("turns:")))' : process.env.VIDEO_TEST_CONFIGURED_ICE === '1' ? 'config.iceServers' : '[]'}});window.__videoPeers.push(this);this.__iceErrors=[];this.addEventListener("icecandidateerror",e=>this.__iceErrors.push({code:e.errorCode,text:e.errorText}));}async createOffer(...args){this.__step="offer-start";const o=await super.createOffer(...args);this.__step="offer-created";return o;}async setLocalDescription(...args){this.__step="local-start";const r=await super.setLocalDescription(...args);this.__step="local-done";return r;}};const gum=navigator.mediaDevices.getUserMedia.bind(navigator.mediaDevices);navigator.mediaDevices.getUserMedia=async function(c){const stream=await gum(c);window.__localStreams.push(stream);return stream;};`});
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
  const {a,b,group}=context;
  const {json}=require('./attachment-test.cjs');
  async function click(page,label){await page.evaluate(`Array.from(document.querySelectorAll('app-video-call button')).find(b=>b.textContent.trim()===${JSON.stringify(label)}).click()`);}
  const callButton = `Array.from(document.querySelectorAll('button')).find(b=>b.getAttribute('aria-label')==='Gọi video')`;
  async function start(page){await until(()=>page.evaluate(`!!(${callButton}) && !(${callButton}).disabled`));await page.evaluate(`(${callButton}).click()`);}
  async function close(page){await click(page,'Đóng');await until(()=>page.evaluate('!document.querySelector(".call-dialog")'));}
  const mediaStopped=page=>page.evaluate('window.__localStreams.every(s=>s.getTracks().every(t=>t.readyState==="ended")) && window.__videoPeers.every(p=>p.connectionState==="closed")');
  async function connected(page){return page.evaluate('(document.querySelector(".call-dialog")?.innerText||"").includes("Đang trong cuộc gọi") && document.querySelector("video.remote")?.videoWidth>0 && window.__videoPeers.some(p=>p.connectionState==="connected")');}
  async function activeCall(){await start(owner);await until(()=>peer.evaluate('(document.querySelector(".call-dialog")?.innerText||"").includes("Cuộc gọi video đến")'));await click(peer,'Nhận cuộc gọi');await until(()=>connected(owner),25000);await until(()=>connected(peer),25000);}
  let owner,peer;
  try {
    await new Promise(r=>server.listen(port,'localhost',r));
    owner=await openBrowser(9420,a,'/chat/'+group);peer=await openBrowser(9421,b,'/contacts');
    await check('UI nút gọi video chỉ sẵn sàng khi hub kết nối',()=>owner.evaluate(`!!(${callButton}) && !(${callButton}).disabled`));
    await until(()=>peer.evaluate('!!document.querySelector("#ct-panel")'));
    await start(owner);
    await check('UI chuông toàn ứng dụng khi người nhận đang ở Danh bạ',async()=> (await peer.text()).includes('Cuộc gọi video đến'));
    await check('UI chưa nhận thì không mở camera người nhận',()=>peer.evaluate('window.__localStreams.length===0'));
    await click(peer,'Từ chối');
    await check('UI người gọi nhận thông báo từ chối',async()=> (await owner.text()).includes('Người kia đã từ chối'));
    await check('UI từ chối dừng mọi track camera người gọi',()=>mediaStopped(owner));
    await close(owner);await close(peer);
    await activeCall();
    await check('UI WebRTC hai bên nhận video thật',async()=>await connected(owner)&&await connected(peer));
    const before=await peer.evaluate('document.querySelector("video.remote").currentTime');await delay(1200);
    await check('UI video remote có frame mới liên tục',async()=>await peer.evaluate('document.querySelector("video.remote").currentTime')>before);
    await check('UI RTP nhận cả âm thanh và video',()=>peer.evaluate('(async()=>{const p=window.__videoPeers.find(p=>p.connectionState==="connected"),s=Array.from((await p.getStats()).values());return ["audio","video"].every(k=>s.some(r=>r.type==="inbound-rtp"&&r.kind===k&&r.bytesReceived>0));})()'));
    if(process.env.VIDEO_TEST_RELAY==='1')await check('UI dùng TURN thật, selected candidate là relay',()=>peer.evaluate('(async()=>{const p=window.__videoPeers.find(p=>p.connectionState==="connected"),s=Array.from((await p.getStats()).values()),t=s.find(r=>r.type==="transport"&&r.selectedCandidatePairId),pair=s.find(r=>r.id===t?.selectedCandidatePairId);return s.find(r=>r.id===pair?.localCandidateId)?.candidateType==="relay";})()'));
    await click(owner,'Tắt microphone');await check('UI tắt microphone thay track.enabled',()=>owner.evaluate('window.__localStreams.slice(-1)[0].getAudioTracks().every(t=>!t.enabled)'));
    await click(owner,'Tắt camera');await check('UI tắt camera thay track.enabled',()=>owner.evaluate('window.__localStreams.slice(-1)[0].getVideoTracks().every(t=>!t.enabled)'));
    await click(owner,'Bật microphone');await click(owner,'Bật camera');
    await check('UI bật lại camera và microphone',()=>owner.evaluate('window.__localStreams.slice(-1)[0].getTracks().every(t=>t.enabled)'));
    await owner.evaluate('document.body.classList.remove("dark")');const light=await owner.call('Page.captureScreenshot',{format:'png'});fs.writeFileSync(path.join(os.tmpdir(),'chatapp-video-light.png'),Buffer.from(light.data,'base64'));
    await owner.evaluate('document.body.classList.add("dark")');await delay(250);const dark=await owner.call('Page.captureScreenshot',{format:'png'});fs.writeFileSync(path.join(os.tmpdir(),'chatapp-video-dark.png'),Buffer.from(dark.data,'base64'));
    await click(owner,'Kết thúc');
    await check('UI kết thúc báo bên kia',async()=> (await peer.text()).includes('Cuộc gọi đã kết thúc'));
    await check('UI hangup dừng camera/mic và đóng peer cả hai bên',async()=>await mediaStopped(owner)&&await mediaStopped(peer));
    await close(owner);await close(peer);
    await peer.evaluate('window.__gumSaved=navigator.mediaDevices.getUserMedia;navigator.mediaDevices.getUserMedia=async()=>{throw Object.assign(new Error("denied"),{name:"NotAllowedError"});}');
    await start(owner);await until(async()=> (await peer.text()).includes('Nhận cuộc gọi'));await click(peer,'Nhận cuộc gọi');
    await check('UI từ chối quyền camera có thông báo dễ hiểu',async()=> (await peer.text()).includes('chưa cho phép sử dụng camera'));
    await check('UI lỗi camera phía nhận giải phóng người gọi',async()=> (await owner.text()).includes('Không thiết lập được cuộc gọi'));
    await close(owner);await close(peer);await peer.evaluate('navigator.mediaDevices.getUserMedia=window.__gumSaved');
    await owner.evaluate('window.__gumSaved=navigator.mediaDevices.getUserMedia;navigator.mediaDevices.getUserMedia=()=>new Promise(resolve=>{window.__resolveMedia=async()=>resolve(await window.__gumSaved({video:true,audio:true}));})');
    await start(owner);await until(()=>owner.evaluate('typeof window.__resolveMedia === "function"'));await click(owner,'Hủy cuộc gọi');
    await owner.evaluate('window.__resolveMedia()');
    await check('UI hủy khi đang xin camera vẫn dừng track trả về muộn',()=>mediaStopped(owner));
    await close(owner);await owner.evaluate('navigator.mediaDevices.getUserMedia=window.__gumSaved');
    await activeCall();await peer.call('Page.navigate',{url:'http://localhost:'+port+'/contacts'});
    await check('UI bên kia tải lại trang thì cuộc gọi kết thúc',async()=> (await owner.text()).includes('Cuộc gọi kết thúc do mất kết nối'));
    await check('UI mất signaling cũng tắt camera',()=>mediaStopped(owner));await close(owner);
    await until(()=>peer.evaluate('!!document.querySelector("#ct-panel")'));
    await activeCall();await json('/api/friends/'+b.id,'DELETE',undefined,a);
    await check('UI hủy kết bạn thu hồi cuộc gọi đang truyền media',async()=> (await owner.text()).includes('quyền truy cập')&&(await peer.text()).includes('quyền truy cập'));
    await check('UI thu hồi quyền đóng cả hai stream',async()=>await mediaStopped(owner)&&await mediaStopped(peer));
    if(errors.length)throw Error(errors.join('\n'));
    console.log('PASS '+checks+' video UI checks; fake camera/microphone, '+(process.env.VIDEO_TEST_RELAY==='1'?'TURN relay':process.env.VIDEO_TEST_CONFIGURED_ICE==='1'?'configured STUN/TURN':'local ICE')+', no paid service.');
  } catch(error) {
    for(let i=0;i<pages.length;i++){try{console.error('Video UI diagnostic '+i+': '+await pages[i].text());console.error(await pages[i].evaluate('JSON.stringify(window.__videoPeers.map(p=>({state:p.connectionState,ice:p.iceConnectionState,signaling:p.signalingState,step:p.__step,errors:p.__iceErrors,iceServers:p.getConfiguration().iceServers.map(s=>s.urls)})))'));const shot=await pages[i].call('Page.captureScreenshot',{format:'png'});fs.writeFileSync(path.join(os.tmpdir(),'chatapp-video-failed-'+i+'.png'),Buffer.from(shot.data,'base64'));}catch{}}
    throw error;
  } finally {
    for(const ws of clients)ws.terminate();for(const socket of sockets)socket.destroy();for(const browser of browsers){try{browser.kill();}catch{}}server.close();await delay(700);
    for(const profile of profiles){const resolved=path.resolve(profile);if(resolved.startsWith(path.resolve(os.tmpdir())+path.sep)&&path.basename(resolved).startsWith('chatapp-video-ui-')){try{fs.rmdirSync(resolved,{recursive:true});}catch{}}}
  }
};
