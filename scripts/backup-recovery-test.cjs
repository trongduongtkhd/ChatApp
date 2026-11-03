// Re-test an existing bundle without creating another backup or source fixtures.
const fs=require('fs'),path=require('path');
const {api,request,check,until,docker}=require('./backup-system-test.cjs');
const root=path.resolve(__dirname,'..');
async function run(){
 const context=JSON.parse(fs.readFileSync(path.join(root,'infra/backups/system/demo-context.local.json'),'utf8'));
 const secrets=JSON.parse(fs.readFileSync(path.join(process.env.APPDATA,'Microsoft/UserSecrets/chatapp-dev/secrets.json'),'utf8').replace(/^\uFEFF/,''));
 const admin=await api('/api/auth/login','POST',{userName:'admin',password:secrets['AdminSeed:Password']});
 if(!process.argv.includes('--observe'))await api('/api/admin/backups/'+context.backupId+'/restore','POST',{},admin);
 await new Promise(r=>setTimeout(r,2000));
 let previous='';
 await until(async()=>{const s=(await api('/api/admin/backups','GET',undefined,admin)).recovery;if(s.status!==previous){previous=s.status;console.log('[RECOVERY] '+s.status+' '+(s.message||''));}if(s.status==='failed')throw Error(s.message);return s.status==='ready';},1200000);
 const user=await api('/api/auth/login','POST',{userName:context.fixtureUser,password:context.fixturePassword},undefined,4201);
 const group=await api('/api/groups/'+context.groupId,'GET',undefined,user,4201);check('Group and account recovered from PostgreSQL',group.group.id===context.groupId);
 const all=[];let before='';while(true){const rows=await api('/api/chat/groups/'+context.groupId+'/messages?limit=100'+before,'GET',undefined,user,4201);all.push(...rows);if(rows.length<100)break;before='&beforeSeq='+rows[0].sequenceNumber;}
 const directory=path.join(root,'infra/backups/system',context.backupId);
 const expected=fs.readFileSync(path.join(directory,'mongo/messages.jsonl'),'utf8').trim().split(/\r?\n/).filter(Boolean).map(line=>JSON.parse(line)).filter(d=>d.groupId===context.groupId).map(d=>d._id).sort();
 check('Recovered Mongo history exactly matches checkpoint',JSON.stringify(all.map(x=>x.id).sort())===JSON.stringify(expected));
 check('Before-backup message recovered',all.some(x=>x.id===context.firstMessageId));
 const content=await request('/api/chat/groups/'+context.groupId+'/attachments/'+context.attachmentId+'/content','GET',undefined,user,4201);
 check('SeaweedFS object bytes match original',content.raw.equals(Buffer.from('Chapter 7 - backup object bytes\nUTF-8: Xin chào')));
 check('Recovery web identifies its mode',(await api('/api/system/mode','GET',undefined,undefined,4201)).recoveryMode===true);
 const recoveredAdmin=await api('/api/auth/login','POST',{userName:'admin',password:secrets['AdminSeed:Password']},undefined,4201);
 check('Recovered scheduler and backup actions are disabled',(await api('/api/admin/backups','GET',undefined,recoveredAdmin,4201)).enabled===false);
 check('Redis AOF is on',docker(['exec','redis','redis-cli','CONFIG','GET','appendonly']).split(/\r?\n/).pop()==='yes');
 check('Replica is read-only standby',docker(['exec','postgres-standby','psql','-U','chatapp','-d','postgres','-tA','-c','SELECT pg_is_in_recovery();'])==='t');
 const old=(await api('/api/admin/backups','GET',undefined,admin)).settings;
 await api('/api/admin/backups/settings','PUT',{period:'hourly',retentionCount:5},admin);
 check('Hangfire schedule saved',(await api('/api/admin/backups','GET',undefined,admin)).settings.period==='hourly');
 await api('/api/admin/backups/settings','PUT',old,admin);
 console.log('PASS: recovered account, group, exact message set and file bytes on web 4201; source unchanged.');
}
run().catch(e=>{console.error(e.stack);process.exitCode=1;});
