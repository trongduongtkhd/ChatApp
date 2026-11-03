// Run the existing full restore test against the new runtime and remove only its artifacts.
const fs=require('fs'),path=require('path'),cp=require('child_process'),{promisify}=require('util');
const {root,runtime}=require('./k8s-common.cjs');
const {api}=require('./backup-system-test.cjs');
const execute=promisify(cp.execFile);
async function main(){
 const contextPath=path.join(root,'infra/backups/system/demo-context.local.json');
 const oldContext=fs.existsSync(contextPath)?fs.readFileSync(contextPath):null;
 const secrets=JSON.parse(fs.readFileSync(path.join(process.env.APPDATA,'Microsoft/UserSecrets/chatapp-dev/secrets.json'),'utf8').replace(/^\uFEFF/,''));
 const admin=await api('/api/auth/login','POST',{userName:'admin',password:secrets['AdminSeed:Password']});
 const before=await api('/api/admin/backups','GET',undefined,admin),existing=new Set(before.items.map(x=>x.id));
 let succeeded=false;
 try{
   const result=await execute(process.execPath,[path.join(__dirname,'backup-system-test.cjs'),'--cleanup-recovery'],{cwd:root,windowsHide:true,timeout:1800000,maxBuffer:8*1024*1024});
   fs.writeFileSync(path.join(runtime,'backup-k8s-run.log'),result.stdout+result.stderr);
   const context=JSON.parse(fs.readFileSync(contextPath));
   const report=path.join(root,'infra/backups/system',context.backupId,'integration-test-report.json');
   fs.copyFileSync(report,path.join(runtime,'backup-k8s-test-report.json'));
   succeeded=true;console.log('PASS: Kubernetes source backup/restore IDs, file bytes, writes and reads; source fixture cleanup verified.');
 }catch(e){fs.writeFileSync(path.join(runtime,'backup-k8s-run.log'),String(e.stdout||'')+String(e.stderr||e.message));throw e;}
 finally{
   await api('/api/admin/backups/recovery','DELETE',undefined,admin);
   await api('/api/admin/backups/settings','PUT',before.settings,admin);
   const after=await api('/api/admin/backups','GET',undefined,admin);
   const base=path.resolve(root,'infra/backups/system');
   for(const item of after.items.filter(x=>!existing.has(x.id))){
     if(!/^\d{8}T\d{6}Z-[a-f0-9]{8}$/.test(item.id))throw Error('Unexpected test bundle name');
     const target=path.resolve(base,item.id);
     if(path.dirname(target)!==base||fs.lstatSync(target).isSymbolicLink())throw Error('Unsafe test bundle path');
     fs.rmSync(target,{recursive:true,force:false});
   }
   if(oldContext)fs.writeFileSync(contextPath,oldContext);else if(fs.existsSync(contextPath))fs.unlinkSync(contextPath);
   console.log('CLEANUP: isolated recovery and bundles created by this suite removed; previous settings restored.');
   if(succeeded){const file=path.join(runtime,'backup-k8s-test-report.json'),report=JSON.parse(fs.readFileSync(file));report.cleanupVerified=true;fs.writeFileSync(file,JSON.stringify(report,null,2));}
 }
}
main().catch(e=>{console.error(e.message);process.exitCode=1;});
