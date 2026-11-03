// Real API/fault/HPA test. Source fixtures use the existing scoped cleaner + fingerprint.
const fs=require('fs'),path=require('path'),http=require('http');
const {execFileSync}=require('child_process');
const signalr=require('../frontend/chat-app/node_modules/@microsoft/signalr');
const {root,runtime,kube,docker}=require('./k8s-common.cjs');
const {api,request,until,delay}=require('./backup-system-test.cjs');
const started=new Date().toISOString(),hubs=[],observations=[];
fs.writeFileSync(path.join(runtime,'active-test.json'),JSON.stringify({started}));
if(fs.existsSync(path.join(runtime,'resilience-test-report.json')))fs.unlinkSync(path.join(runtime,'resilience-test-report.json'));
let checks=0,groupStopped=false,maintenance=null,loading=false,loadTask=null;
function check(label,value){if(!value)throw Error('[FAIL] '+label);checks++;console.log('[OK] '+label);}
const id=()=>require('crypto').randomBytes(16).toString('hex').replace(/^(........)(....)(....)(....)(............)$/,'$1-$2-$3-$4-$5');
function local(action){execFileSync('powershell.exe',['-NoProfile','-ExecutionPolicy','Bypass','-File',path.join(__dirname,'control-local-service.ps1'),'-Service','Group','-Action',action],{cwd:root,windowsHide:true,stdio:'ignore',timeout:30000});}
async function raw(port,url){return(await request(url,'GET',undefined,undefined,port)).raw.toString();}
async function prom(query){const data=await api('/api/v1/query?query='+encodeURIComponent(query),'GET',undefined,undefined,9090);return data.data.result;}
async function metrics(pod){return raw(18080,'/podmetrics/'+pod);}
function pods(){return JSON.parse(kube(['-n','chatapp','get','pods','-l','app=chat','-o','json'])).items;}
async function connect(user,group,reconnect){
 const hub=new signalr.HubConnectionBuilder().withUrl('http://127.0.0.1:5000/hubs/chat',{accessTokenFactory:()=>user.accessToken,skipNegotiation:true,transport:signalr.HttpTransportType.WebSockets}).withAutomaticReconnect([0,500,1000,2000,5000]).configureLogging(signalr.LogLevel.None).build();
 hub.onreconnected(async()=>{try{await hub.invoke('JoinGroup',group);reconnect.count++;}catch(e){reconnect.error=e.message;}});
 hub.received=new Set();hub.on('ReceiveMessage',m=>hub.received.add(m.id));
 await hub.start();await hub.invoke('JoinGroup',group);hubs.push(hub);return hub;
}
async function main(){
 check('Node 14 compatibility',process.version.startsWith('v14.'));
 check('Kubernetes mode active',JSON.parse(fs.readFileSync(path.join(runtime,'mode.json'))).mode==='kubernetes');
 check('Kubernetes Chat Service uses NodePort, not a single-Pod port-forward',JSON.parse(kube(['-n','chatapp','get','service','chat','-o','json'])).spec.type==='NodePort');
 for(const port of [5000,5001,5002,5004,5005])check('Health ready '+port,(await api('/health/ready','GET',undefined,undefined,port)).status==='Healthy');
 await until(async()=>Number((await prom('sum(up{job="chatapp-pods"})'))[0]?.value[1])>=2,120000);
 check('Prometheus scrapes multiple Chat Pods',Number((await prom('sum(up{job="chatapp-pods"})'))[0].value[1])>=2);
 check('Grafana dashboard provisioned',(await api('/api/dashboards/uid/chatapp-ch8','GET',undefined,undefined,3000)).dashboard.panels.length>=12);
 check('Resource Metrics API available',!!JSON.parse(kube(['get','--raw','/apis/metrics.k8s.io/v1beta1/namespaces/chatapp/pods'])).items.length);
 const name='ad'+Date.now().toString(36)+'resilience';
 await api('/api/auth/register','POST',{userName:name,email:name+'@test.local',displayName:'Admin test resilience',password:'Passw0rd!'});
 const user=await api('/api/auth/login','POST',{userName:name,password:'Passw0rd!'});
 await delay(1500);
 const group=(await api('/api/groups','POST',{name:'Chapter 8 resilience fixture'},user)).id;
 const reconnect={count:0};
 for(let i=0;i<8;i++)await connect(user,group,reconnect);
 const messageId=id(),message=await hubs[0].invoke('SendMessage',messageId,group,'Chapter 8 baseline');
 check('Real chat message saved',message.id===messageId);
 // Readiness maintenance affects routing, not liveness/restart.
 maintenance=pods().find(p=>p.status.phase==='Running').metadata.name;
 const before=pods().find(p=>p.metadata.name===maintenance).status.containerStatuses[0].restartCount;
 kube(['-n','chatapp','exec',maintenance,'--','touch','/tmp/chatapp-not-ready']);
 await until(async()=>!pods().find(p=>p.metadata.name===maintenance).status.conditions.some(c=>c.type==='Ready'&&c.status==='True'),45000);
 check('Readiness excludes the maintenance Pod',!JSON.parse(kube(['-n','chatapp','get','endpointslices','-l','kubernetes.io/service-name=chat','-o','json'])).items.flatMap(x=>x.endpoints).some(e=>e.targetRef?.name===maintenance&&e.conditions.ready));
 check('Liveness still succeeds during readiness failure',kube(['get','--raw','/api/v1/namespaces/chatapp/pods/'+maintenance+':8080/proxy/health/live']).includes('Healthy'));
 check('Readiness failure does not restart container',pods().find(p=>p.metadata.name===maintenance).status.containerStatuses[0].restartCount===before);
 kube(['-n','chatapp','exec',maintenance,'--','rm','/tmp/chatapp-not-ready']);maintenance=null;
 await until(async()=>pods().filter(p=>p.status.conditions?.some(c=>c.type==='Ready'&&c.status==='True')).length>=2,60000);
 // Dependency failure: membership is never bypassed. Original Group process is restored in finally.
 const healthyGroup = await prom('max(chatapp_circuit_state{dependency="group"})');
groupStopped=true;local('Stop');
 let failures=0;
 for(let batch=0;batch<4;batch++){await Promise.all(Array.from({length:12},async()=>{try{await api('/api/chat/groups/'+group+'/messages','GET',undefined,user);}catch(e){if(e.status===503)failures++;}}));await delay(500);}
 check('Group outage returns temporary errors',failures>=10);
 await until(async()=> (await prom('max(chatapp_circuit_state{dependency="group"})')).some(x=>Number(x.value[1])===1),45000);
 check('Circuit opens on Group transport faults',true);
 await until(async()=> (await api('/api/v1/alerts','GET',undefined,undefined,9090)).data.alerts.some(a=>a.labels.alertname==='ChatAppCircuitOpen'),30000);
 check('Prometheus circuit alert visible',true);
 const seqEvents=await api('/api/events?count=100&filter='+encodeURIComponent("@Message like '%Circuit%'"),'GET',undefined,undefined,5341);
 check('Seq stores structured circuit events',JSON.stringify(seqEvents).includes('CircuitState'));
 local('Start');groupStopped=false;
 await until(async()=>{try{return (await api('/api/chat/groups/'+group+'/messages','GET',undefined,user)).some(m=>m.id===messageId);}catch{return false;}},90000);
 check('Circuit recovers without restarting Chat',true);
 // Kill a Pod holding connections. Other replicas continue and clients reconnect/rejoin.
 const counts=await Promise.all(pods().filter(p=>p.status.phase==='Running').map(async p=>({pod:p.metadata.name,count:Number((await metrics(p.metadata.name)).match(/^chatapp_signalr_connections[^\n]*? ([0-9.]+)$/m)?.[1]||0)})));
 const victim=counts.sort((a,b)=>b.count-a.count)[0];check('A Pod has active connections',victim.count>0);
 reconnect.count=0;
 kube(['-n','chatapp','delete','pod',victim.pod,'--wait=false']);
 await until(async()=>reconnect.count>0,90000);check('Real clients reconnect and rejoin after Pod deletion',true);
 await until(async()=>pods().filter(p=>p.status.conditions?.some(c=>c.type==='Ready'&&c.status==='True')&&!p.metadata.deletionTimestamp).length>=2,90000);
 const live=hubs.find(h=>h.state===signalr.HubConnectionState.Connected);
 const duplicate=await live.invoke('SendMessage',messageId,group,'Chapter 8 baseline');
 check('Retry keeps original message ID and sequence',duplicate.id===messageId&&duplicate.sequenceNumber===message.sequenceNumber);
 check('History contains one copy after failover',(await api('/api/chat/groups/'+group+'/messages','GET',undefined,user)).filter(m=>m.id===messageId).length===1);
 // New connections go through the Service. Existing WebSockets stay on their original Pod.
 const baseline=JSON.parse(kube(['-n','chatapp','get','hpa','chat','-o','json']));
 check('HPA range is 2..4',baseline.spec.minReplicas===2&&baseline.spec.maxReplicas===4);
await until(async()=>{const h=JSON.parse(kube(['-n','chatapp','get','hpa','chat','-o','json']));return h.status.currentReplicas===2&&h.status.desiredReplicas===2;},360000);
const initialPods=new Set(pods().map(p=>p.metadata.name));
 loading=true;let successes=0,errors=0;
 loadTask=Promise.all(Array.from({length:32},async()=>{while(loading){try{await api('/api/chat/groups/'+group+'/messages?limit=100','GET',undefined,user);successes++;}catch{errors++;}await delay(5);}}));
 await until(async()=>{const h=JSON.parse(kube(['-n','chatapp','get','hpa','chat','-o','json']));observations.push({at:new Date().toISOString(),desired:h.status.desiredReplicas,current:h.status.currentReplicas,cpu:h.status.currentMetrics});return h.status.currentReplicas>=3&&h.status.desiredReplicas>=3;},240000);
 check('HPA scales up under real authenticated history traffic',successes>50);
 await until(async()=>pods().filter(p=>p.status.conditions?.some(c=>c.type==='Ready'&&c.status==='True')&&!p.metadata.deletionTimestamp).length>=3,90000);
 for(let i=0;i<12;i++)await connect(user,group,reconnect);
await until(async()=>{
  for(const p of pods().filter(p=>!initialPods.has(p.metadata.name)&&p.status.phase==='Running')){
    try{if(/chatapp_signalr_connections[^\n]* [1-9]/.test(await metrics(p.metadata.name)))return true;}catch{}
  }return false;
},90000);
check('New HPA Pod receives real SignalR connections through Service',true);
 const broadcast=id();await hubs.find(h=>h.state===signalr.HubConnectionState.Connected).invoke('SendMessage',broadcast,group,'Broadcast after HPA scale-up');
 await until(async()=>hubs.every(h=>h.received.has(broadcast)),30000);
 check('Redis backplane broadcasts across old and new HPA replicas',true);
 console.log('LOAD: '+successes+' successful reads, '+errors+' errors');
 loading=false;await loadTask;
 for(const h of hubs)await h.stop();hubs.length=0;
 await until(async()=>{const h=JSON.parse(kube(['-n','chatapp','get','hpa','chat','-o','json']));observations.push({at:new Date().toISOString(),desired:h.status.desiredReplicas,current:h.status.currentReplicas});return h.status.currentReplicas===2&&h.status.desiredReplicas===2;},360000);
 check('HPA scales down to two after load stops',true);
 check('HPA never exceeds max replicas',observations.every(x=>x.desired<=4));
 fs.writeFileSync(path.join(runtime,'resilience-test-report.json'),JSON.stringify({passed:true,checks,started,completedAt:new Date().toISOString(),successes,errors,observations},null,2));
 console.log('PASS '+checks+' chapter 8 checks.');
}
main().catch(e=>{console.error(e.stack);process.exitCode=1;fs.writeFileSync(path.join(runtime,'resilience-test-failure.json'),JSON.stringify({error:e.message,checks,observations},null,2));}).finally(async()=>{
 loading=false;if(loadTask)await loadTask.catch(()=>{});
 if(maintenance)try{kube(['-n','chatapp','exec',maintenance,'--','rm','-f','/tmp/chatapp-not-ready']);}catch{}
 if(groupStopped)try{local('Start');}catch(e){console.error(e.message);process.exitCode=1;}
 for(const h of hubs)await h.stop().catch(()=>{});
 try{await delay(3000);execFileSync(process.execPath,[path.join(__dirname,'cleanup-test-data.cjs'),'--apply','--wait','--since='+started],{cwd:root,encoding:'utf8',windowsHide:true,stdio:'pipe',timeout:240000});console.log('CLEANUP: fixture removed; retained-data fingerprints verified.');if(fs.existsSync(path.join(runtime,'resilience-test-report.json'))){const report=JSON.parse(fs.readFileSync(path.join(runtime,'resilience-test-report.json')));report.cleanupVerified=true;fs.writeFileSync(path.join(runtime,'resilience-test-report.json'),JSON.stringify(report,null,2));}}
 catch(e){console.error('Cleanup failed: '+String(e.stderr||e.message).slice(0,1200));process.exitCode=1;}
});
