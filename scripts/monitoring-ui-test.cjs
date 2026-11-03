const fs=require('fs'),path=require('path'),http=require('http'),{spawn}=require('child_process');
const WebSocket=require('../frontend/chat-app/node_modules/ws');
const {runtime}=require('./k8s-common.cjs');
const {until,delay}=require('./backup-system-test.cjs');
const profile=fs.mkdtempSync(path.join(runtime,'monitor-ui-'));let browser,ws,checks=0;const errors=[];
function get(url){return new Promise((r,j)=>http.get(url,s=>{let b='';s.on('data',d=>b+=d);s.on('end',()=>{try{r(JSON.parse(b));}catch(e){j(e);}});}).on('error',j));}
function check(label,value){if(!value)throw Error(label);checks++;console.log('[OK] '+label);}
async function main(){
 browser=spawn('C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe',['--headless=new','--disable-gpu','--no-first-run','--remote-debugging-port=9497','--user-data-dir='+profile,'about:blank'],{windowsHide:true,stdio:'ignore'});
 let targets;await until(async()=>{try{targets=await get('http://127.0.0.1:9497/json/list');return !!targets.length;}catch{return false;}},20000);
 ws=new WebSocket(targets.find(t=>t.type==='page').webSocketDebuggerUrl);await new Promise((r,j)=>{ws.once('open',r);ws.once('error',j);});
 let seq=0;const pending=new Map();
 ws.on('message',raw=>{const m=JSON.parse(raw);if(m.id&&pending.has(m.id)){const p=pending.get(m.id);pending.delete(m.id);m.error?p.j(Error(JSON.stringify(m.error))):p.r(m.result);}if(m.method==='Runtime.exceptionThrown')errors.push(m.params.exceptionDetails.text);});
 function call(method,params={}){return new Promise((r,j)=>{const id=++seq;pending.set(id,{r,j});ws.send(JSON.stringify({id,method,params}));});}
 async function evaluate(expression){const out=await call('Runtime.evaluate',{expression,returnByValue:true,awaitPromise:true});if(out.exceptionDetails)throw Error(out.exceptionDetails.text);return out.result.value;}
 await call('Page.enable');await call('Runtime.enable');await call('Emulation.setDeviceMetricsOverride',{width:1600,height:1100,deviceScaleFactor:1,mobile:false});
 await call('Page.navigate',{url:'http://localhost:3000/d/chatapp-ch8'});
 await until(()=>evaluate('(document.body?.innerText||"").includes("Service readiness") && (document.body?.innerText||"").includes("Active SignalR connections")'),60000);
 await delay(5000);check('Grafana renders provisioned dashboard',true);
 check('Grafana has no visible datasource error',!(await evaluate('(document.body?.innerText||"")')).includes('Data source not found'));
 fs.writeFileSync(path.join(runtime,'grafana.png'),Buffer.from((await call('Page.captureScreenshot',{format:'png'})).data,'base64'));
 await call('Page.navigate',{url:'http://localhost:5341'});await until(()=>evaluate('(document.body?.innerText||"").includes("Events")'),60000);check('Seq web renders events view',true);
 fs.writeFileSync(path.join(runtime,'seq.png'),Buffer.from((await call('Page.captureScreenshot',{format:'png'})).data,'base64'));
 await call('Page.navigate',{url:'http://localhost:4200/auth/login'});
 await until(()=>evaluate('!!document.querySelector("#u") && !!document.querySelector("#p")'),30000);check('Angular login still renders',true);
 check('No uncaught browser JavaScript errors',errors.length===0);
 fs.writeFileSync(path.join(runtime,'monitoring-ui-test-report.json'),JSON.stringify({passed:true,checks,errors},null,2));
}
main().catch(e=>{console.error(e.stack);process.exitCode=1;}).finally(async()=>{if(ws)ws.close();if(browser)browser.kill();await delay(1500);const target=path.resolve(profile);if(path.dirname(target)!==path.resolve(runtime)||!path.basename(target).startsWith('monitor-ui-'))throw Error('Unsafe UI profile cleanup path');try{fs.rmSync(target,{recursive:true,force:true,maxRetries:5,retryDelay:200});}catch(e){console.error('Own UI profile still locked: '+e.message);}});
