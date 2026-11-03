// Host bridge for Kubernetes API metrics. Only loopback; no arbitrary API paths.
// Prometheus discovers each Pod separately, preserving that process's metric labels.
const fs=require('fs'),path=require('path'),http=require('http'),cp=require('child_process'),{promisify}=require('util');
const {runtime,kubeconfig,mode,atomic}=require('./k8s-common.cjs');
const exec=promisify(cp.execFile),args=['--kubeconfig',kubeconfig];
let text='# operations agent starting\n',pods=new Set();
const k=async a=>(await exec('kubectl',[...args,...a],{windowsHide:true,timeout:12000,maxBuffer:8*1024*1024})).stdout;
const quantity=(s,type)=>s.endsWith('n')?parseFloat(s)/1e9:s.endsWith('u')?parseFloat(s)/1e6:s.endsWith('m')?parseFloat(s)/1000:s.endsWith('Ki')?parseFloat(s)*1024:s.endsWith('Mi')?parseFloat(s)*1048576:s.endsWith('Gi')?parseFloat(s)*1073741824:parseFloat(s);
async function sample(){
  if(mode().mode!=='kubernetes'){
    atomic(path.join(runtime,'pod-targets.json'),[{targets:['host.docker.internal:5003','host.docker.internal:5013']}]);
    text='chatapp_operations_agent_up 1\n';return;
  }
  // Nginx routes through NodePort; a service port-forward would pin traffic to one Pod.
  const snapshot=JSON.parse(await k(['-n','chatapp','get','pods','-l','app=chat','-o','json']));
  const running=snapshot.items.filter(p=>p.status.phase==='Running'&&!p.metadata.deletionTimestamp);
  pods=new Set(running.map(p=>p.metadata.name));
  atomic(path.join(runtime,'pod-targets.json'),running.map(p=>({targets:['host.docker.internal:18080'],labels:{__metrics_path__:'/podmetrics/'+p.metadata.name,pod:p.metadata.name}})));
  const lines=['chatapp_operations_agent_up 1'];
  for(const p of snapshot.items){
    const label='{pod="'+p.metadata.name+'"}';
    lines.push('chatapp_pod_ready'+label+' '+((p.status.conditions||[]).some(c=>c.type==='Ready'&&c.status==='True')?1:0));
    lines.push('chatapp_pod_restarts'+label+' '+(p.status.containerStatuses||[]).reduce((a,c)=>a+c.restartCount,0));
  }
  try{
    const metrics=JSON.parse(await k(['get','--raw','/apis/metrics.k8s.io/v1beta1/namespaces/chatapp/pods']));
    for(const p of metrics.items){const label='{pod="'+p.metadata.name+'"}';lines.push('chatapp_pod_cpu_cores'+label+' '+p.containers.reduce((a,c)=>a+quantity(c.usage.cpu),0));lines.push('chatapp_pod_memory_bytes'+label+' '+p.containers.reduce((a,c)=>a+quantity(c.usage.memory),0));}
    lines.push('chatapp_resource_metrics_up 1');
  }catch{lines.push('chatapp_resource_metrics_up 0');}
  try{const h=JSON.parse(await k(['-n','chatapp','get','hpa','chat','-o','json']));lines.push('chatapp_hpa_desired_replicas '+(h.status.desiredReplicas||0),'chatapp_hpa_current_replicas '+(h.status.currentReplicas||0));}catch{}
  text=lines.join('\n')+'\n';
}
const server=http.createServer(async(req,res)=>{
  if(req.url==='/metrics'){res.setHeader('content-type','text/plain; version=0.0.4');res.end(text);return;}
  const match=/^\/podmetrics\/(chat-[a-z0-9-]+)$/.exec(req.url);
  if(match&&pods.has(match[1]))try{res.setHeader('content-type','text/plain; version=0.0.4');res.end(await k(['get','--raw','/api/v1/namespaces/chatapp/pods/'+match[1]+':8080/proxy/metrics']));return;}catch{}
  res.statusCode=503;res.end('Pod metrics unavailable');
});
server.listen(18080,'127.0.0.1');
server.on('error',e=>{console.error(e.message);process.exit(1);});
(async()=>{while(true){try{await sample();}catch(e){text='chatapp_operations_agent_up 0\n';console.error(e.message);}await new Promise(r=>setTimeout(r,5000));}})();
process.on('SIGINT',()=>{server.close();process.exit();});
