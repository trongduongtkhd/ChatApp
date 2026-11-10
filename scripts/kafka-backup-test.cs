#:project ../backend/src/BuildingBlocks/ChatApp.Common/ChatApp.Common.csproj
#:property PublishAot=false
// Two disposable brokers; no source/recovery topics or consumer offsets are changed.
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using ChatApp.Common.Backup;
using Confluent.Kafka;
using Confluent.Kafka.Admin;

var suffix = Guid.NewGuid().ToString("N")[..10];
var names = new[] { "chatapp-backup-test-source-" + suffix, "chatapp-backup-test-target-" + suffix };
var ports = new int[2];
var root = Path.GetFullPath(Path.Combine("infra", "backups", "runtime", "kafka-" + suffix));
Directory.CreateDirectory(root);
var checks = 0;
void Check(string label, bool value) { if (!value) throw new Exception(label); Console.WriteLine("PASS " + ++checks + " - " + label); }
string Docker(params string[] arguments)
{
    var start = new ProcessStartInfo("docker.exe") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
    foreach (var argument in arguments) start.ArgumentList.Add(argument);
    using var p = Process.Start(start)!;
    var output = p.StandardOutput.ReadToEndAsync(); var error = p.StandardError.ReadToEndAsync();
    if (!p.WaitForExit(120000)) { p.Kill(true); throw new TimeoutException("Docker test tool timeout"); }
    Task.WaitAll(output, error);
    if (p.ExitCode != 0) throw new Exception(error.Result);
    return output.Result.Trim();
}
IConsumer<string,string> Reader(string address, string group) => new ConsumerBuilder<string,string>(new ConsumerConfig {
    BootstrapServers = address, GroupId = group, EnableAutoCommit = false, EnablePartitionEof = true, SocketTimeoutMs = 3000 }).Build();
async Task SetOffsets(IAdminClient admin, ConsumerGroupTopicPartitionOffsets value)
{
    var deadline=DateTime.UtcNow.AddSeconds(60);
    while(true)
    {
        try { await admin.AlterConsumerGroupOffsetsAsync([value]); return; }
        catch(AlterConsumerGroupOffsetsException ex) when(DateTime.UtcNow<deadline)
        { Console.WriteLine("Waiting for fresh consumer coordinator: "+string.Join(",",ex.Results.Select(r=>r.Error.Reason))); await Task.Delay(1000); }
    }
}
try
{
    for (var i = 0; i < 2; i++)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); ports[i] = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        Docker("run", "-d", "--name", names[i], "--label", "chatapp.backup.test=" + suffix, "-p", $"127.0.0.1:{ports[i]}:9092",
            "-e", "KAFKA_NODE_ID=1", "-e", "KAFKA_PROCESS_ROLES=broker,controller", "-e", "KAFKA_CONTROLLER_QUORUM_VOTERS=1@localhost:9093",
            "-e", "KAFKA_CONTROLLER_LISTENER_NAMES=CONTROLLER", "-e", "KAFKA_LISTENERS=PLAINTEXT://:9092,CONTROLLER://:9093",
            "-e", $"KAFKA_ADVERTISED_LISTENERS=PLAINTEXT://127.0.0.1:{ports[i]}", "-e", "KAFKA_LISTENER_SECURITY_PROTOCOL_MAP=PLAINTEXT:PLAINTEXT,CONTROLLER:PLAINTEXT",
            "-e", "KAFKA_INTER_BROKER_LISTENER_NAME=PLAINTEXT", "-e", "KAFKA_OFFSETS_TOPIC_REPLICATION_FACTOR=1", "-e", "KAFKA_AUTO_CREATE_TOPICS_ENABLE=false",
            "-e", "KAFKA_HEAP_OPTS=-Xmx256m -Xms256m", "apache/kafka:4.1.0");
    }
    var source = "127.0.0.1:" + ports[0]; var target = "127.0.0.1:" + ports[1];
    foreach (var address in new[] {source, target})
    {
        using var ready = new AdminClientBuilder(new AdminClientConfig {BootstrapServers = address, SocketTimeoutMs=2000}).Build();
        var deadline = DateTime.UtcNow.AddSeconds(90);
        while (true) { try { ready.GetMetadata(TimeSpan.FromSeconds(2)); break; } catch { if (DateTime.UtcNow > deadline) throw; await Task.Delay(500); } }
    }
    using var admin = new AdminClientBuilder(new AdminClientConfig { BootstrapServers = source }).Build();
    await admin.CreateTopicsAsync(KafkaBackup.Consumers.Keys.Select(t => new TopicSpecification { Name=t, NumPartitions=3, ReplicationFactor=1 }));
    using var producer = new ProducerBuilder<string,string>(new ProducerConfig { BootstrapServers=source, Acks=Acks.All }).Build();
    foreach (var topic in KafkaBackup.Consumers.Keys)
        for (var partition=0; partition<3; partition++)
            for (var n=0; n<5; n++) await producer.ProduceAsync(new TopicPartition(topic, partition), new Message<string,string> {Key="key-"+n, Value="before-"+n});
    var groups = KafkaBackup.Consumers.SelectMany(pair => pair.Value.Select(group => (pair.Key, group))).GroupBy(x=>x.group);
    foreach (var group in groups)
    {
        var offset = group.Key == "notification-service" ? 4 : 2;
        var values = group.SelectMany(x => Enumerable.Range(0,3).Select(p=>new TopicPartitionOffset(x.Key,p,offset))).ToList();
        await SetOffsets(admin,new ConsumerGroupTopicPartitionOffsets(group.Key,values));
    }
    var checkpoint = KafkaBackup.Capture(source);
    Check("Captured independent consumer offsets and high watermarks", checkpoint.Partitions.All(p=>p.End==5) && checkpoint.Partitions.Any(p=>p.Groups.Values.Distinct().Count()==2));
    foreach (var p in checkpoint.Partitions)
        await producer.ProduceAsync(new TopicPartition(p.Topic,p.Partition),new Message<string,string>{Key="late",Value="AFTER CHECKPOINT"});
    await KafkaBackup.ExportAsync(source,checkpoint,root,CancellationToken.None);
    Check("Export excludes records after checkpoint", !Directory.EnumerateFiles(root,"*.jsonl").Any(f=>File.ReadAllText(f).Contains("AFTER CHECKPOINT")));
    // A fully caught-up source exports no pending records. No ProduceAsync call
    // occurs during import, so the first group offset request hits a cold coordinator.
    var emptyCheckpoint = new KafkaBackup.Checkpoint(checkpoint.Partitions.Select(p =>
        p with { Start=p.End, Groups=p.Groups.ToDictionary(g=>g.Key,g=>p.End) }).ToArray());
    var emptyRoot=Path.Combine(root,"empty");
    await KafkaBackup.ExportAsync(source,emptyCheckpoint,emptyRoot,CancellationToken.None);
    try { await KafkaBackup.ImportAsync(target,emptyRoot,CancellationToken.None); }
    catch (AlterConsumerGroupOffsetsException ex)
    {
        foreach (var result in ex.Results)
        {
            Console.Error.WriteLine($"Cold restore offset failure: group={result.Group}, error={result.Error.Code}: {result.Error.Reason}");
            foreach (var partition in result.Partitions.Where(p=>p.Error.IsError))
                Console.Error.WriteLine($"Partition {partition.TopicPartition}: {partition.Error.Code}: {partition.Error.Reason}");
        }
        throw;
    }
    foreach(var p in emptyCheckpoint.Partitions)
        foreach(var group in p.Groups.Keys)
        {
            using var consumer=Reader(target,group);
            Check($"Caught-up backup {group}/{p.Topic}/{p.Partition}: restored offset is zero",
                consumer.Committed([new TopicPartition(p.Topic,p.Partition)],TimeSpan.FromSeconds(5)).Single().Offset.Value==0);
        }
    try { await KafkaBackup.ImportAsync(target,root,CancellationToken.None); }
    catch (AlterConsumerGroupOffsetsException ex)
    {
        foreach (var result in ex.Results)
        {
            Console.Error.WriteLine($"Restore offset failure: group={result.Group}, error={result.Error.Code}: {result.Error.Reason}");
            foreach (var partition in result.Partitions.Where(p=>p.Error.IsError))
                Console.Error.WriteLine($"Partition {partition.TopicPartition}: {partition.Error.Code}: {partition.Error.Reason}");
        }
        throw;
    }
    foreach (var p in checkpoint.Partitions)
    {
        using var reader=Reader(target,"test-read-"+suffix);
        var tp=new TopicPartition(p.Topic,p.Partition);
        var high=reader.QueryWatermarkOffsets(tp,TimeSpan.FromSeconds(5)).High.Value;
        Check($"{tp}: retained pending range restored",high==p.End-p.Start);
        reader.Assign(new TopicPartitionOffset(tp,0));
        for(var n=p.Start;n<p.End;n++)
        {
            var item=reader.Consume(TimeSpan.FromSeconds(10));
            if(item is null || item.IsPartitionEOF || item.Message.Value!="before-"+n || item.Message.Key!="key-"+n) throw new Exception("Event content/order mismatch");
        }
        foreach(var g in p.Groups)
        {
            using var consumer=Reader(target,g.Key);
            var value=consumer.Committed([tp],TimeSpan.FromSeconds(5)).Single().Offset.Value;
            Check($"{g.Key}/{tp}: offset mapped to new broker",value==g.Value-p.Start);
        }
    }
    var refused=false;
    try {await KafkaBackup.ImportAsync(target,root,CancellationToken.None);} catch(InvalidOperationException){refused=true;}
    Check("Import refuses an occupied target",refused);
    var first=checkpoint.Partitions.First(p=>p.Groups.Count>0);
    await admin.AlterConsumerGroupOffsetsAsync([new ConsumerGroupTopicPartitionOffsets(first.Groups.Keys.First(),[new TopicPartitionOffset(first.Topic,first.Partition,99)])]);
    refused=false;
    try{KafkaBackup.Capture(source);}catch(InvalidOperationException){refused=true;}
    Check("Invalid source consumer boundary is rejected",refused);
    await File.WriteAllTextAsync(Path.Combine(root,"test-report.json"),JsonSerializer.Serialize(new{checks,passed=true}));
    Console.WriteLine($"PASS: {checks} Kafka backup checks; test containers cleaned in finally.");
}
finally
{
    foreach(var name in names)
    {
        try
        {
            var label=Docker("inspect","--format","{{index .Config.Labels \"chatapp.backup.test\"}}",name);
            if(label==suffix && name.StartsWith("chatapp-backup-test-",StringComparison.Ordinal)) Docker("rm","-f","-v",name);
        }
        catch(Exception ex){Console.Error.WriteLine("Test cleanup: "+ex.Message);}
    }
}
