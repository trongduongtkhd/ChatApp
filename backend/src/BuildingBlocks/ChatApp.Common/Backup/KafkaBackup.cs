using System.Text.Json;
using ChatApp.Contracts.Events;
using Confluent.Kafka;
using Confluent.Kafka.Admin;

namespace ChatApp.Common.Backup;

// Preserve only the retained range needed by the consumers at the checkpoint.
// Original offsets are metadata, not assumed to equal offsets in a fresh broker.
public static class KafkaBackup
{
    public record PartitionPoint(string Topic, int Partition, long Start, long End, Dictionary<string, long> Groups);
    public record Checkpoint(PartitionPoint[] Partitions);
    public record Event(long Offset, string? Key, string? Value, long Timestamp);
    public static readonly Dictionary<string, string[]> Consumers = new()
    {
        [KafkaTopics.UserRegistered] = ["group-service"], [KafkaTopics.FriendshipChanged] = ["group-service"],
        [KafkaTopics.MemberAdded] = ["chat-service", "notification-service"],
        [KafkaTopics.MemberRemoved] = ["chat-service", "notification-service"],
        [KafkaTopics.ChatMessageSent] = ["notification-service"], [KafkaTopics.AccountStateChanged] = []
    };
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
    private static IConsumer<string, string> Reader(string bootstrap, string group) => new ConsumerBuilder<string, string>(new ConsumerConfig
    { BootstrapServers = bootstrap, GroupId = group, EnableAutoCommit = false, EnableAutoOffsetStore = false, EnablePartitionEof = true, SocketTimeoutMs = 5000 }).Build();

    public static Checkpoint Capture(string bootstrap)
    {
        using var admin = new AdminClientBuilder(new AdminClientConfig { BootstrapServers = bootstrap, SocketTimeoutMs = 3000 }).Build();
        var metadata = admin.GetMetadata(TimeSpan.FromSeconds(3));
        using var reader = Reader(bootstrap, "backup-metadata-" + Guid.NewGuid().ToString("N"));
        var points = new List<PartitionPoint>();
        foreach (var topic in Consumers)
        {
            var details = metadata.Topics.SingleOrDefault(t => t.Topic == topic.Key) ?? throw new InvalidOperationException("Missing topic " + topic.Key);
            foreach (var partition in details.Partitions)
            {
                var tp = new TopicPartition(topic.Key, partition.PartitionId);
                var watermarks = reader.QueryWatermarkOffsets(tp, TimeSpan.FromSeconds(3));
                var groups = new Dictionary<string, long>();
                foreach (var group in topic.Value)
                {
                    using var consumer = Reader(bootstrap, group);
                    var offset = consumer.Committed([tp], TimeSpan.FromSeconds(3)).Single().Offset.Value;
                    if (offset < 0) offset = watermarks.Low.Value;
                    if (offset < watermarks.Low.Value || offset > watermarks.High.Value)
                        throw new InvalidOperationException("Consumer offset outside retained data: " + group + "/" + tp);
                    groups[group] = offset;
                }
                points.Add(new(topic.Key, partition.PartitionId, groups.Count == 0 ? watermarks.Low.Value : groups.Values.Min(), watermarks.High.Value, groups));
            }
        }
        return new(points.ToArray());
    }
    public static async Task ExportAsync(string bootstrap, Checkpoint checkpoint, string directory, CancellationToken ct)
    {
        Directory.CreateDirectory(directory);
        foreach (var point in checkpoint.Partitions)
        {
            using var consumer = Reader(bootstrap, "backup-export-" + Guid.NewGuid().ToString("N"));
            consumer.Assign(new TopicPartitionOffset(point.Topic, point.Partition, point.Start));
            var low = consumer.QueryWatermarkOffsets(new TopicPartition(point.Topic, point.Partition), TimeSpan.FromSeconds(3)).Low.Value;
            if (low > point.Start) throw new InvalidOperationException("Kafka retention removed events during backup.");
            await using var writer = new StreamWriter(Path.Combine(directory, Filename(point)));
            var deadline = DateTime.UtcNow.AddMinutes(5);
            while (point.Start < point.End)
            {
                ct.ThrowIfCancellationRequested();
                if (DateTime.UtcNow > deadline) throw new TimeoutException("Kafka backup range incomplete.");
                var item = consumer.Consume(TimeSpan.FromMilliseconds(250));
                if (item is null) continue;
                if (item.IsPartitionEOF) { if (item.Offset.Value >= point.End) break; continue; }
                if (item.Offset.Value >= point.End) break;
                await writer.WriteLineAsync(JsonSerializer.Serialize(new Event(item.Offset.Value, item.Message.Key, item.Message.Value, item.Message.Timestamp.UnixTimestampMs)));
                if (consumer.Position(new TopicPartition(point.Topic, point.Partition)).Value >= point.End) break;
            }
        }
        await File.WriteAllTextAsync(Path.Combine(directory, "checkpoint.json"), JsonSerializer.Serialize(checkpoint, Json), ct);
    }
    public static async Task ImportAsync(string bootstrap, string directory, CancellationToken ct)
    {
        var checkpoint = JsonSerializer.Deserialize<Checkpoint>(await File.ReadAllTextAsync(Path.Combine(directory, "checkpoint.json"), ct), Json)!;
        using var admin = new AdminClientBuilder(new AdminClientConfig { BootstrapServers = bootstrap }).Build();
        var topics = checkpoint.Partitions.GroupBy(p => p.Topic).Select(g => new TopicSpecification { Name = g.Key, NumPartitions = g.Max(p => p.Partition) + 1, ReplicationFactor = 1 });
        // This command is only invoked inside the isolated recovery network.
        var existing = admin.GetMetadata(TimeSpan.FromSeconds(5)).Topics.Select(t => t.Topic).ToHashSet();
        foreach (var specification in topics)
            if (!existing.Contains(specification.Name)) await admin.CreateTopicsAsync([specification]);
        var commits = new Dictionary<string, List<TopicPartitionOffset>>();
        using var producer = new ProducerBuilder<string, string>(new ProducerConfig { BootstrapServers = bootstrap, Acks = Acks.All, EnableIdempotence = true }).Build();
        foreach (var point in checkpoint.Partitions)
        {
            using var checker = Reader(bootstrap, "restore-verify-" + Guid.NewGuid().ToString("N"));
            if (checker.QueryWatermarkOffsets(new TopicPartition(point.Topic, point.Partition), TimeSpan.FromSeconds(5)).High.Value != 0)
                throw new InvalidOperationException("Recovery Kafka partition is not empty.");
            var events = new List<Event>();
            using var reader = File.OpenText(Path.Combine(directory, Filename(point)));
            while (await reader.ReadLineAsync(ct) is { } line)
            {
                var item = JsonSerializer.Deserialize<Event>(line)!;
                if (item.Offset < point.Start || item.Offset >= point.End || (events.Count > 0 && item.Offset <= events[^1].Offset)) throw new InvalidOperationException("Invalid Kafka event range.");
                events.Add(item);
                var delivery = await producer.ProduceAsync(new TopicPartition(point.Topic, point.Partition),
                    new Message<string, string> { Key = item.Key!, Value = item.Value!, Timestamp = new Timestamp(item.Timestamp, TimestampType.CreateTime) }, ct);
                if (delivery.Offset.Value != events.Count - 1) throw new InvalidOperationException("Unexpected recovered Kafka offsets.");
            }
            foreach (var group in point.Groups)
            {
                if (!commits.TryGetValue(group.Key, out var offsets)) commits[group.Key] = offsets = [];
                offsets.Add(new TopicPartitionOffset(point.Topic, point.Partition, events.LongCount(e => e.Offset < group.Value)));
            }
        }
        foreach (var group in commits)
            await RestoreGroupOffsetsAsync(admin, new ConsumerGroupTopicPartitionOffsets(group.Key, group.Value), ct);
        // A successful API call is not sufficient: verify every restored bookmark
        // before recovery consumers are allowed to start.
        foreach (var group in commits)
        {
            ct.ThrowIfCancellationRequested();
            using var checker = Reader(bootstrap, group.Key);
            var actual = checker.Committed(group.Value.Select(p => p.TopicPartition), TimeSpan.FromSeconds(5))
                .ToDictionary(p => p.TopicPartition, p => p.Offset.Value);
            foreach (var expected in group.Value)
                if (!actual.TryGetValue(expected.TopicPartition, out var offset) || offset != expected.Offset.Value)
                    throw new InvalidOperationException($"Recovered Kafka offset mismatch: {group.Key}/{expected.TopicPartition}.");
        }
    }

    private static async Task RestoreGroupOffsetsAsync(IAdminClient admin, ConsumerGroupTopicPartitionOffsets offsets, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await admin.AlterConsumerGroupOffsetsAsync([offsets],
                    new AlterConsumerGroupOffsetsOptions { RequestTimeout = TimeSpan.FromSeconds(5) });
                return;
            }
            catch (AlterConsumerGroupOffsetsException ex)
            {
                // Kafka may report success at group level while individual
                // partitions fail, especially on a fresh __consumer_offsets topic.
                var errors = ex.Results.SelectMany(r => new[] { r.Error }
                    .Concat(r.Partitions.Select(p => p.Error))).Where(e => e.IsError).ToArray();
                var detail = string.Join("; ", errors.Select(e => $"{e.Code}: {e.Reason}"));
                if (errors.Length == 0 || errors.Any(e => !CoordinatorStarting(e)) || DateTime.UtcNow >= deadline)
                    throw new InvalidOperationException($"Cannot restore Kafka offsets for {offsets.Group}: {detail}", ex);
                Console.Error.WriteLine($"Waiting for recovery Kafka coordinator ({offsets.Group}): {detail}");
            }
            catch (KafkaException ex) when (CoordinatorStarting(ex.Error) && DateTime.UtcNow < deadline)
            {
                Console.Error.WriteLine($"Waiting for recovery Kafka coordinator ({offsets.Group}): {ex.Error.Code}");
            }
            await Task.Delay(500, ct);
        }
    }

    private static bool CoordinatorStarting(Error error) => error.Code is
        ErrorCode.GroupCoordinatorNotAvailable or ErrorCode.NotCoordinatorForGroup or ErrorCode.GroupLoadInProgress or
        ErrorCode.RequestTimedOut or ErrorCode.Local_TimedOut or ErrorCode.Local_WaitCoord or
        ErrorCode.Local_Transport or ErrorCode.NetworkException or ErrorCode.BrokerNotAvailable;

    private static string Filename(PartitionPoint point) => point.Topic + "-" + point.Partition + ".jsonl";
}
