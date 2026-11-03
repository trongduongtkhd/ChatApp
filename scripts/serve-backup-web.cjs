// Production Angular preview on the user's normal web port. Node 14, no packages.
const http=require('http'),fs=require('fs'),path=require('path');
const root=path.resolve(__dirname,'../frontend/chat-app/dist/chat-app');
const server=http.createServer((req,res)=>{
  if(req.url.startsWith('/api/')||req.url.startsWith('/hubs/')){
    const p=http.request({hostname:'127.0.0.1',port:5000,path:req.url,method:req.method,headers:req.headers},up=>{res.writeHead(up.statusCode,up.headers);up.pipe(res);});
    p.on('error',()=>{if(!res.headersSent)res.writeHead(502,{'Content-Type':'application/json'});res.end(JSON.stringify({title:'Gateway chưa sẵn sàng.'}));});req.pipe(p);return;
  }
  let file;try{file=path.resolve(root,'.'+decodeURIComponent(req.url.split('?')[0]));}catch{res.writeHead(400);res.end();return;}
  if(file!==root&&!file.startsWith(root+path.sep)){res.writeHead(403);res.end();return;}
  if(!fs.existsSync(file)||!fs.statSync(file).isFile())file=path.join(root,'index.html');
  res.setHeader('Content-Type',({'.html':'text/html; charset=utf-8','.js':'application/javascript','.css':'text/css','.svg':'image/svg+xml','.woff2':'font/woff2'})[path.extname(file)]||'application/octet-stream');
  const stream=fs.createReadStream(file);stream.on('error',()=>res.destroy());stream.pipe(res);
});
server.on('upgrade',(req,socket,head)=>{
  const p=http.request({hostname:'127.0.0.1',port:5000,path:req.url,headers:req.headers});
  p.on('upgrade',(up,remote,remoteHead)=>{
    socket.write('HTTP/1.1 101 Switching Protocols\r\n'+Object.entries(up.headers).map(([k,v])=>k+': '+v).join('\r\n')+'\r\n\r\n');
    if(remoteHead.length)socket.write(remoteHead);if(head.length)remote.write(head);socket.pipe(remote).pipe(socket);
    socket.on('error',()=>remote.destroy());remote.on('error',()=>socket.destroy());socket.on('close',()=>remote.destroy());remote.on('close',()=>socket.destroy());
  });p.on('response',()=>socket.destroy());p.on('error',()=>socket.destroy());p.end();
});
server.on('clientError',(_err,socket)=>socket.destroy());
server.listen(4200,'127.0.0.1',()=>console.log('ChatApp web: http://localhost:4200'));
