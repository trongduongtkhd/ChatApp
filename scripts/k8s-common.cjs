// Node 14. No dependencies; private kubeconfig stays inside ignored runtime.
const fs=require('fs'),path=require('path'),cp=require('child_process');
const root=path.resolve(__dirname,'..'),runtime=path.join(root,'infra/backups/runtime/chapter8');
fs.mkdirSync(runtime,{recursive:true});
const kubeconfig=path.join(runtime,'kubeconfig');
function run(exe,args,options={}){return cp.execFileSync(exe,args,{cwd:root,encoding:'utf8',windowsHide:true,stdio:'pipe',timeout:180000,...options}).trim();}
function kube(args,options){return run('kubectl',['--kubeconfig',kubeconfig,...args],options);}
function docker(args,options){return run('docker',args,options);}
function apply(value){return kube(['apply','-f','-'],{input:JSON.stringify(value)});}
function mode(){try{return JSON.parse(fs.readFileSync(path.join(runtime,'mode.json'),'utf8'));}catch{return{mode:'compose'};}}
function atomic(file,value){const temp=file+'.tmp';fs.writeFileSync(temp,JSON.stringify(value,null,2));fs.renameSync(temp,file);}
module.exports={root,runtime,kubeconfig,run,kube,docker,apply,mode,atomic};
