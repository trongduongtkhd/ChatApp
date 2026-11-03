// Standalone business-load demo, without stopping services. Node 14.
const fs=require('fs'),path=require('path'),{execFileSync}=require('child_process');
const {root,runtime,mode,kube}=require('./k8s-common.cjs');
const {api,delay,until}=require('./backup-system-test.cjs');
const started=new Date().toISOString();let running=true,successes=0,errors=0,created=false;
const seconds=Number((process.argv.find(a=>a.startsWith('--seconds='))||'--seconds=90').split('=')[1]);
const workers=Number((process.argv.find(a=>a.startsWith('--workers='))||'--workers=32').split('=')[1]);
process.on('SIGINT',()=>{running=false;console.log('Stopping load, then cleaning fixture...');});
async function main(){
 if(mode().mode!=='kubernetes')throw Error('Start Kubernetes mode first.');
 if(!Number.isInteger(seconds)||seconds<10||seconds>600||!Number.isInteger(workers)||workers<1||workers>64)throw Error('Use seconds 10..600, workers 1..64.');
 const userName='ad'+Date.now().toString(36)+'hpa';
 await api('/api/auth/register','POST',{userName,email:userName+'@test.local',displayName:'Admin test HPA',password:'Passw0rd!'});created=true;
 const user=await api('/api/auth/login','POST',{userName,password:'Passw0rd!'});
 const group=(await api('/api/groups','POST',{name:'Chapter 8 HPA load fixture'},user)).id;
 const deadline=Date.now()+seconds*1000;let last=0,maxDesired=0;
 const tasks=Array.from({length:workers},async()=>{while(running&&Date.now()<deadline){try{await api('/api/chat/groups/'+group+'/messages?limit=100','GET',undefined,user);successes++;}catch{errors++;}await delay(5);}});
 while(running&&Date.now()<deadline){
  const h=JSON.parse(kube(['-n','chatapp','get','hpa','chat','-o','json']));maxDesired=Math.max(maxDesired,h.status.desiredReplicas||0);
  console.log(JSON.stringify({reads:successes,errors,current:h.status.currentReplicas,desired:h.status.desiredReplicas,cpu:h.status.currentMetrics}));
  await delay(5000);
 }
 running=false;await Promise.all(tasks);
 fs.writeFileSync(path.join(runtime,'hpa-load-report.json'),JSON.stringify({started,seconds,workers,successes,errors,maxDesired},null,2));
 console.log('Load ended. Observe scale-down with kubectl get hpa -w; fixture cleanup follows.');
}
main().catch(e=>{console.error(e.message);process.exitCode=1;}).finally(()=>{
 if(created)try{execFileSync(process.execPath,[path.join(__dirname,'cleanup-test-data.cjs'),'--apply','--wait','--since='+started],{cwd:root,windowsHide:true,stdio:'ignore',timeout:240000});console.log('Fixture removed; retained-data fingerprints verified.');}catch(e){console.error(e.message);process.exitCode=1;}
});
