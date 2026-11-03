// Planned failure checks. Run after other backup suites finish.
const fs=require('fs'),path=require('path'),crypto=require('crypto');
const {api,check,until,delay,docker}=require('./backup-system-test.cjs');
const root=path.resolve(__dirname,'..'),token=crypto.randomBytes(16).toString('hex'),key='chatapp:backup:test:'+token;
let admin,paused=false,redisRestarted=false;
async function main(){
  const secrets=JSON.parse(fs.readFileSync(path.join(process.env.APPDATA,'Microsoft/UserSecrets/chatapp-dev/secrets.json'),'utf8').replace(/^\uFEFF/,''));
  admin=await api('/api/auth/login','POST',{userName:'admin',password:secrets['AdminSeed:Password']});
  const state=await api('/api/admin/backups','GET',undefined,admin);
  if(state.items.some(x=>['queued','running'].includes(x.status))||['queued','running'].includes(state.recovery.status))throw Error('Wait for active backup/restore jobs before this planned failure test.');
  check('Physical standby streams from primary',docker(['exec','postgres','psql','-U','chatapp','-d','postgres','-tA','-c',"SELECT count(*) FROM pg_stat_replication WHERE state='streaming';"])==='1');
  check('Standby rejects writes by default',docker(['exec','postgres-standby','psql','-U','chatapp','-d','postgres','-tA','-c','SHOW transaction_read_only;'])==='on');
  check('Redis AOF policy is everysec',docker(['exec','redis','redis-cli','CONFIG','GET','appendfsync']).split(/\r?\n/).pop()==='everysec');
  docker(['exec','redis','redis-cli','SET',key,token,'EX','300']);
  await delay(1500); // Allow the configured everysec AOF fsync.
  docker(['restart','redis']);redisRestarted=true;
  await until(async()=>{try{return docker(['exec','redis','redis-cli','PING'])==='PONG';}catch{return false;}},30000);
  check('Own test value survives Redis restart',docker(['exec','redis','redis-cli','GET',key])===token);
  await until(async()=>docker(['exec','redis','redis-cli','GET','chatapp:backup:instance:identity']).length>0,30000);
  await delay(2000);
  const set=docker(['exec','redis','redis-cli','SET','chatapp:backup:intent',token,'NX','PX','8000']);
  check('Test acquired an expiring checkpoint intent',set==='OK');
  await until(async()=>docker(['exec','redis','redis-cli','GET','chatapp:backup:ack:identity']).endsWith(':'+token),7000);
  check('Identity drained and acknowledged this exact intent',docker(['exec','redis','redis-cli','PTTL','chatapp:backup:intent'])>1000);
  const started=Date.now();
  await api('/api/auth/login','POST',{userName:'admin',password:secrets['AdminSeed:Password']});
  const elapsed=Date.now()-started;
  check('Writes resume when a lost coordinator intent expires',elapsed>=800&&elapsed<10000&&docker(['exec','redis','redis-cli','GET','chatapp:backup:intent'])==='');
  docker(['pause','chat-service-2']);paused=true;
  await delay(11000); // Heartbeat expires; the other Chat replica remains available.
  const id=(await api('/api/admin/backups','POST',{},admin)).id;
  let failure;
  await until(async()=>{failure=(await api('/api/admin/backups','GET',undefined,admin)).items.find(x=>x.id===id);return failure&&['failed','complete'].includes(failure.status);},120000);
  check('Missing participant makes backup fail closed',failure.status==='failed'&&failure.message.includes('chat-2'));
  check('Failed cut is never published as a complete bundle',!fs.existsSync(path.join(root,'infra/backups/system',id,'manifest.json')));
  docker(['unpause','chat-service-2']);paused=false;
  await until(async()=>docker(['exec','redis','redis-cli','GET','chatapp:backup:instance:chat-2']).length>0,30000);
  check('Application login works after planned faults',!!(await api('/api/auth/login','POST',{userName:'admin',password:secrets['AdminSeed:Password']})).accessToken);
  fs.writeFileSync(path.join(root,'infra/backups/runtime/backup-infra-test-report.json'),JSON.stringify({passed:true,failedBackup:id,intentReleaseMs:elapsed,redisRestarted},null,2));
  console.log('PASS infrastructure checks; only own temporary key removed.');
}
main().catch(e=>{console.error(e.stack);process.exitCode=1;}).finally(()=>{
  if(paused)try{docker(['unpause','chat-service-2']);}catch(e){console.error('Unpause required: '+e.message);process.exitCode=1;}
  try{docker(['exec','redis','redis-cli','EVAL',"if redis.call('GET',KEYS[1])==ARGV[1] then return redis.call('DEL',KEYS[1]) else return 0 end",'1','chatapp:backup:intent',token]);docker(['exec','redis','redis-cli','DEL',key]);}catch(e){console.error('Own Redis key cleanup: '+e.message);process.exitCode=1;}
});
