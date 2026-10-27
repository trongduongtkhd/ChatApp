// Khởi tạo thực sự hai shard; chạy lại an toàn, không drop dữ liệu.
function worked(result) { if (!result.ok) throw new Error(JSON.stringify(result)); return result; }
async function wait(action, label) {
  for (let i = 0; i < 180; i++) {
    try { return await action(); } catch (e) {
      if (i === 179) throw new Error(label + ': ' + e.message);
      await new Promise(resolve => setTimeout(resolve, 1000));
    }
  }
}
async function main() {
  const nodes = [
    { host: 'mongo-config', name: 'chat-config', port: 27019, config: true },
    { host: 'mongo-shard-1', name: 'chat-shard-1', port: 27018 },
    { host: 'mongo-shard-2', name: 'chat-shard-2', port: 27018 }
  ];
  for (const node of nodes) {
    print('Initializing ' + node.name);
    const admin = await wait(async () => {
      const connection = await new Mongo('mongodb://' + node.host + ':' + node.port + '/?directConnection=true');
      const a = connection.getDB('admin'); worked(await a.runCommand({ ping: 1 })); return a;
    }, node.name);
    try { worked(await admin.runCommand({ replSetGetStatus: 1 })); }
    catch (error) {
      if (error.code && error.code !== 94) throw error;
      worked(await admin.runCommand({ replSetInitiate: {
        _id: node.name, ...(node.config ? { configsvr: true } : {}),
        members: [{ _id: 0, host: node.host + ':' + node.port }]
      } }));
    }
    await wait(async () => {
      const hello = await admin.runCommand({ hello: 1 });
      if (!hello.isWritablePrimary) throw new Error('primary not ready');
      return true;
    }, node.name);
    print(node.name + ' primary ready');
  }
  const router = await wait(async () => {
    const m = await new Mongo('mongodb://mongo-router:27017');
    worked(await m.getDB('admin').runCommand({ ping: 1 })); return m;
  }, 'mongos');
  const admin = router.getDB('admin');
  for (const node of nodes.filter(n => !n.config))
    worked(await admin.runCommand({ addShard: node.name + '/' + node.host + ':' + node.port }));
  const name = 'chat_history';
  worked(await admin.runCommand({ enableSharding: name }));
  const chat = router.getDB(name);
  for (const [collection, key] of [
    ['messages', { groupId: 'hashed' }], ['message_keys', { _id: 'hashed' }],
    ['conversation_state', { _id: 'hashed' }], ['outbox_messages', { groupId: 'hashed' }]
  ]) {
    if (!(await chat.getCollectionNames()).includes(collection)) worked(await chat.createCollection(collection));
    await chat[collection].createIndex(key);
    const info = await router.getDB('config').collections.findOne({ _id: name + '.' + collection, dropped: { $ne: true } });
    if (!info) worked(await admin.runCommand({ shardCollection: name + '.' + collection, key, numInitialChunks: 4 }));
  }
  await chat.messages.createIndex({ groupId: 1, sequenceNumber: -1 }, { unique: true, name: 'group_sequence_unique' });
  await chat.messages.createIndex({ groupId: 1, weekStartUtc: 1, sequenceNumber: -1 }, { name: 'group_week_sequence' });
  await chat.outbox_messages.createIndex({ groupId: 1, processedAt: 1, sequenceNumber: 1 }, { name: 'group_pending_sequence' });
  await chat.conversation_state.createIndex({ hasPending: 1, leaseUntil: 1 }, { name: 'pending_lease' });
  await chat.system_metadata.updateOne({ _id: 'schema' }, { $set: { version: 1, initializedAt: new Date() } }, { upsert: true });
  print('Mongo 2 shards ready. Chạy migration --apply trước khi mở Chat.');
}
main().catch(error => { print(error.stack); quit(1); });

