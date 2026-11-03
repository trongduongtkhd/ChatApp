// Tests an actual stopped application process; no database fixtures or public fault endpoints.
const fs=require('fs'),path=require('path');
const {runtime,kube,docker}=require('./k8s-common.cjs');
const {until}=require('./backup-system-test.cjs');
let selected=null,checks=0,hostPid=null,containerId=null;
function pod(){return JSON.parse(kube(['-n','chatapp','get','pod',selected,'-o','json']));}
async function main(){
 const list=JSON.parse(kube(['-n','chatapp','get','pods','-l','app=chat','-o','json'])).items;
 selected=list.find(p=>!p.metadata.deletionTimestamp&&p.status.conditions?.some(c=>c.type==='Ready'&&c.status==='True')).metadata.name;
 const before=pod().status.containerStatuses[0].restartCount;
 const container=JSON.parse(docker(['exec','chatapp-control-plane','crictl','ps','--name','chat','-o','json'])).containers.find(c=>c.labels['io.kubernetes.pod.name']===selected);
 if(!container)throw Error('Own Chat container not found');
 containerId=container.id;
 hostPid=JSON.parse(docker(['exec','chatapp-control-plane','crictl','inspect',containerId])).info.pid;
 if(!Number.isInteger(hostPid)||hostPid<2)throw Error('Invalid container process PID');
 docker(['exec','chatapp-control-plane','kill','-STOP',String(hostPid)]);
 console.log('Stopped own Chat process from parent PID namespace.');
 await until(async()=>pod().status.containerStatuses[0].restartCount>before,150000);checks++;console.log('[OK] Liveness restarts stopped process');
 await until(async()=>pod().status.conditions.some(c=>c.type==='Ready'&&c.status==='True'),90000);checks++;console.log('[OK] Restarted container becomes Ready');
 fs.writeFileSync(path.join(runtime,'probe-test-report.json'),JSON.stringify({passed:true,checks,pod:selected,restartsBefore:before,restartsAfter:pod().status.containerStatuses[0].restartCount},null,2));
}
main().catch(e=>{console.error(e.message);process.exitCode=1;}).finally(()=>{if(hostPid&&containerId)try{const original=JSON.parse(docker(['exec','chatapp-control-plane','crictl','inspect',containerId]));if(original.status.state==='CONTAINER_RUNNING'&&original.info.pid===hostPid)docker(['exec','chatapp-control-plane','kill','-CONT',String(hostPid)]);}catch{}});
