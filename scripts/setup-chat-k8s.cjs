const fs=require('fs'),path=require('path'),crypto=require('crypto');
const {root,runtime,kubeconfig,run,kube,docker,apply,atomic}=require('./k8s-common.cjs');
const kind=path.join(runtime,'kind.exe');
async function main(){
  if(!fs.existsSync(kind)){
    run('curl.exe',['-fL','--retry','2','https://kind.sigs.k8s.io/dl/v0.33.0/kind-windows-amd64','-o',kind]);
    const sum=run('curl.exe',['-fsL','https://kind.sigs.k8s.io/dl/v0.33.0/kind-windows-amd64.sha256sum']).split(/\s/)[0];
    if(crypto.createHash('sha256').update(fs.readFileSync(kind)).digest('hex')!==sum)throw Error('kind checksum mismatch');
  }
  const clusters=run(kind,['get','clusters']).split(/\s+/);
  if(!clusters.includes('chatapp'))run(kind,['create','cluster','--name','chatapp','--kubeconfig',kubeconfig,'--wait','180s'],{timeout:600000});
  else if(!fs.existsSync(kubeconfig))run(kind,['export','kubeconfig','--name','chatapp','--kubeconfig',kubeconfig]);
  console.log('Kubernetes cluster ready. Preparing Chat image and dependencies.');
  kube(['get','nodes']);
  // Connect the kind node to the existing data network. Source volumes remain unchanged.
  const source=JSON.parse(docker(['inspect','chat-service-1']))[0];
  const network=Object.keys(source.NetworkSettings.Networks)[0];
  const node=JSON.parse(docker(['inspect','chatapp-control-plane']))[0];
  if(!node.NetworkSettings.Networks[network])docker(['network','connect',network,'chatapp-control-plane']);
  const aliases=['redis','mongo-router','attachment-storage','kafka','presence-service'].map(name=>{
    const data=JSON.parse(docker(['inspect',name]))[0];
    return {ip:data.NetworkSettings.Networks[network].IPAddress,hostnames:[name]};
  });
  const host=docker(['exec','chatapp-control-plane','getent','ahostsv4','host.docker.internal']).split(/\s+/)[0];
  aliases.push({ip:host,hostnames:['host.docker.internal']});
  docker(['tag','chatapp/chat-service','chatapp/chat-service:chapter8']);
  run(kind,['load','docker-image','chatapp/chat-service:chapter8','--name','chatapp'],{timeout:300000});
  apply({apiVersion:'v1',kind:'Namespace',metadata:{name:'chatapp',labels:{'app.kubernetes.io/part-of':'chatapp'}}});
  // Reuse effective source config privately; never print secrets or put them in tracked YAML.
  const env=Object.fromEntries(source.Config.Env.filter(x=>x.includes('=')).map(x=>{const i=x.indexOf('=');return[x.slice(0,i),x.slice(i+1)];}));
  for(const key of ['PATH','HOSTNAME','HOME','DOTNET_RUNNING_IN_CONTAINER','APP_UID','DOTNET_VERSION','ASPNET_VERSION','Backup__InstanceId'])delete env[key];
  env.Operations__SeqUrl='http://host.docker.internal:5341';env.Backup__CoordinationEnabled='true';
  env.Operations__ReadinessFile='/tmp/chatapp-not-ready';env.ASPNETCORE_URLS='http://+:8080';env.DOTNET_GCHeapHardLimit='0x10000000';
  apply({apiVersion:'v1',kind:'Secret',metadata:{name:'chat-config',namespace:'chatapp'},type:'Opaque',stringData:env});
  apply({apiVersion:'apps/v1',kind:'Deployment',metadata:{name:'chat',namespace:'chatapp'},spec:{replicas:2,
    revisionHistoryLimit:2,strategy:{type:'RollingUpdate',rollingUpdate:{maxSurge:1,maxUnavailable:0}},selector:{matchLabels:{app:'chat'}},
    template:{metadata:{labels:{app:'chat'}},spec:{terminationGracePeriodSeconds:45,hostAliases:aliases,containers:[{
      name:'chat',image:'chatapp/chat-service:chapter8',imagePullPolicy:'Never',ports:[{containerPort:8080,name:'http'}],
      envFrom:[{secretRef:{name:'chat-config'}}],env:[{name:'Backup__InstanceId',valueFrom:{fieldRef:{fieldPath:'metadata.name'}}}],
      resources:{requests:{cpu:'200m',memory:'160Mi'},limits:{cpu:'1000m',memory:'512Mi'}},
      startupProbe:{httpGet:{path:'/health/live',port:8080},periodSeconds:3,failureThreshold:60},
      livenessProbe:{httpGet:{path:'/health/live',port:8080},periodSeconds:10,failureThreshold:3},
      readinessProbe:{httpGet:{path:'/health/ready',port:8080},periodSeconds:5,timeoutSeconds:4,failureThreshold:2}
    }]}}}});
  apply({apiVersion:'v1',kind:'Service',metadata:{name:'chat',namespace:'chatapp'},spec:{type:'NodePort',selector:{app:'chat'},ports:[{name:'http',port:8080,targetPort:8080,nodePort:30080}]}});
  const components=path.join(runtime,'metrics-server.yaml');
  if(!fs.existsSync(components))run('curl.exe',['-fL','https://github.com/kubernetes-sigs/metrics-server/releases/download/v0.8.0/components.yaml','-o',components]);
  kube(['apply','-f',components]);
  kube(['-n','kube-system','patch','deployment','metrics-server','--type=json','-p',JSON.stringify([{op:'add',path:'/spec/template/spec/containers/0/args/-',value:'--kubelet-insecure-tls'}])]);
  kube(['-n','chatapp','rollout','status','deployment/chat','--timeout=180s'],{timeout:200000});
  kube(['-n','kube-system','rollout','status','deployment/metrics-server','--timeout=180s'],{timeout:200000});
  // Only switch ownership after new Pods are Ready. Drain old source writers first.
  docker(['compose','stop','chat-service-1','chat-service-2']);
  atomic(path.join(runtime,'mode.json'),{mode:'kubernetes',kubeconfig,namespace:'chatapp',deployment:'chat',updatedAt:new Date().toISOString()});
  console.log('Chat Kubernetes mode selected. Start operations agent, then route Nginx to Kubernetes NodePort.');
}
main().catch(e=>{console.error(e.message);process.exitCode=1;});
