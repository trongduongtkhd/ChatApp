using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ChatApp.ChatService.Dtos;
using ChatApp.Common.Kafka;
using ChatApp.Contracts.Events;
using MongoDB.Bson;
using MongoDB.Driver;
using Npgsql;

namespace ChatApp.ChatService.Data.LegacyImport;

// Chỉ dùng khi --migrate-chat; không đăng ký ChatDbContext/EF vào API chạy thật.
// Source PostgreSQL chỉ đọc, target chỉ INSERT hoặc nâng floor, không ghi đè dữ liệu khác khóa.
public static class ChatHistoryMigration
{
    public static async Task RunAsync(IConfiguration configuration, string[] args)
    {
        var apply = args.Contains("--apply");
        var verify = args.Contains("--verify");
        if (apply && !args.Contains("--offline")) throw new InvalidOperationException("--apply yêu cầu --offline sau khi dừng hai Chat Service.");
        var store = new MongoChatStore(configuration);
        await using var pg = new NpgsqlConnection(configuration.GetConnectionString("ChatDb"));
        await pg.OpenAsync();
        await using var sourceTx = await pg.BeginTransactionAsync(IsolationLevel.RepeatableRead);
        await using (var readOnly = new NpgsqlCommand("SET TRANSACTION READ ONLY", pg, sourceTx)) await readOnly.ExecuteNonQueryAsync();
        var groups = new Dictionary<string, long>();
        var pendingGroups = new HashSet<string>();
        long messageCount = 0, outboxCount = 0;
        using var sourceHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var targetHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        await using (var command = new NpgsqlCommand("SELECT id,group_id,sender_id,sender_name,content,sequence_number,created_at FROM messages ORDER BY group_id,sequence_number", pg, sourceTx))
        await using (var reader = await command.ExecuteReaderAsync())
        {
            var batch = new List<MessageDto>(100);
            while (await reader.ReadAsync())
            {
                batch.Add(new(reader.GetGuid(0), reader.GetGuid(1), reader.GetGuid(2), reader.GetString(3),
                    reader.GetString(4), reader.GetInt64(5), reader.GetFieldValue<DateTimeOffset>(6)));
                if (batch.Count == 100) { await MigrateMessagesAsync(batch); batch.Clear(); }
            }
            if (batch.Count > 0) await MigrateMessagesAsync(batch);
        }

        await using (var command = new NpgsqlCommand("SELECT id,topic,key,payload::text,occurred_at,processed_at,attempts,last_error FROM outbox_messages ORDER BY occurred_at,id", pg, sourceTx))
        await using (var reader = await command.ExecuteReaderAsync())
        {
            var batch = new List<BsonDocument>(100);
            while (await reader.ReadAsync())
            {
                if (reader.GetString(1) != KafkaTopics.ChatMessageSent) throw new InvalidOperationException("Chat Outbox có topic chưa hỗ trợ; dừng để không bỏ sót.");
                var payload = reader.GetString(3);
                var evt = JsonSerializer.Deserialize<MessageSent>(payload, KafkaJson.Options) ?? throw new InvalidOperationException("Outbox không hợp lệ");
                var group = evt.GroupId.ToString();
                if (reader.GetGuid(0) != evt.EventId || reader.GetString(2) != group) throw new InvalidOperationException("Outbox key/eventId không khớp");
                if (reader.IsDBNull(5)) pendingGroups.Add(group);
                batch.Add(new BsonDocument
                {
                    ["_id"] = evt.EventId.ToString(), ["groupId"] = group, ["messageId"] = evt.MessageId.ToString(),
                    ["sequenceNumber"] = evt.SequenceNumber, ["topic"] = reader.GetString(1), ["payload"] = payload,
                    ["occurredAt"] = reader.GetFieldValue<DateTimeOffset>(4).UtcDateTime,
                    ["processedAt"] = reader.IsDBNull(5) ? BsonNull.Value : new BsonDateTime(reader.GetFieldValue<DateTimeOffset>(5).UtcDateTime),
                    ["attempts"] = reader.GetInt32(6), ["lastError"] = reader.IsDBNull(7) ? BsonNull.Value : reader.GetString(7)
                });
                if (batch.Count == 100) { await MigrateOutboxAsync(batch); batch.Clear(); }
            }
            if (batch.Count > 0) await MigrateOutboxAsync(batch);
        }
        if (apply)
        {
            foreach (var (group, max) in groups)
            {
                var update = Builders<BsonDocument>.Update.Max("lastSeq", max)
                    .SetOnInsert("leaseUntil", DateTime.UnixEpoch).SetOnInsert("leaseVersion", 0L);
                update = pendingGroups.Contains(group) ? update.Set("hasPending", true) : update.SetOnInsert("hasPending", false);
                await store.States.UpdateOneAsync(new BsonDocument("_id", group), update, new UpdateOptions { IsUpsert = true });
            }
        }
        var sourceDigest = Convert.ToHexString(sourceHash.GetHashAndReset());
        var targetDigest = Convert.ToHexString(targetHash.GetHashAndReset());
        if ((apply || verify) && sourceDigest != targetDigest) throw new InvalidOperationException("Checksum migration không khớp");
        if (apply) await store.Database.GetCollection<BsonDocument>("system_metadata").UpdateOneAsync(
            new BsonDocument("_id", "storage-ready"), Builders<BsonDocument>.Update.Set("completed", true)
                .Set("verifiedAt", DateTime.UtcNow).Set("sourceMessages", messageCount).Set("sourceOutbox", outboxCount)
                .Set("sourceSha256", sourceDigest), new UpdateOptions { IsUpsert = true });
        Console.WriteLine(JsonSerializer.Serialize(new { mode = apply ? "apply" : verify ? "verify" : "dry-run",
            messages = messageCount, outbox = outboxCount, groups = groups.Count, pendingGroups = pendingGroups.Count,
            sourceSha256 = sourceDigest, targetSha256 = apply || verify ? targetDigest : null, verified = apply || verify }));

        async Task MigrateMessagesAsync(List<MessageDto> batch)
        {
            foreach (var m in batch)
            {
                groups[m.GroupId.ToString()] = Math.Max(groups.GetValueOrDefault(m.GroupId.ToString()), m.SequenceNumber);
                sourceHash.AppendData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(m, KafkaJson.Options) + "\n"));
            }
            if (apply || verify)
            {
                using var session = await store.Client.StartSessionAsync();
                await session.WithTransactionAsync(async (s, ct) =>
                {
                    // Hash chỉ cập nhật SAU callback vì callback có thể chạy lại.
                    foreach (var m in batch)
                    {
                        var filter = new BsonDocument { ["_id"] = m.Id.ToString(), ["groupId"] = m.GroupId.ToString() };
                        var existing = await store.Messages.Find(s, filter).FirstOrDefaultAsync(ct);
                        if (existing is null)
                        {
                            if (!apply) throw new InvalidOperationException($"Thiếu message {m.Id}");
                            await store.Messages.InsertOneAsync(s, MongoChatStore.Document(m), cancellationToken: ct);
                        }
                        else if (MongoChatStore.ToDto(existing) != m || existing["weekStartUtc"].ToUniversalTime() != Services.ChatWeek.StartUtc(m.CreatedAt))
                            throw new InvalidOperationException($"Message khác nội dung/thời gian: {m.Id}");
                        var key = await store.Keys.Find(s, new BsonDocument("_id", m.Id.ToString())).FirstOrDefaultAsync(ct);
                        if (key is null)
                        {
                            if (!apply) throw new InvalidOperationException($"Thiếu key {m.Id}");
                            await store.Keys.InsertOneAsync(s, new BsonDocument { ["_id"] = m.Id.ToString(), ["groupId"] = m.GroupId.ToString(), ["senderId"] = m.SenderId.ToString() }, cancellationToken: ct);
                        }
                        else if (key["groupId"].AsString != m.GroupId.ToString() || key["senderId"].AsString != m.SenderId.ToString())
                            throw new InvalidOperationException($"Key bị xung đột: {m.Id}");
                    }
                    return true;
                }, MongoChatStore.TransactionOptions);
                foreach (var m in batch)
                {
                    var target = await store.Messages.Find(new BsonDocument { ["_id"] = m.Id.ToString(), ["groupId"] = m.GroupId.ToString() }).FirstAsync();
                    targetHash.AppendData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(MongoChatStore.ToDto(target), KafkaJson.Options) + "\n"));
                }
            }
            messageCount += batch.Count;
            if (messageCount % 1000 == 0) Console.WriteLine($"messages verified/read: {messageCount}");
        }

        async Task MigrateOutboxAsync(List<BsonDocument> batch)
        {
            if (apply || verify)
            {
                using var session = await store.Client.StartSessionAsync();
                await session.WithTransactionAsync(async (s, ct) =>
                {
                    foreach (var doc in batch)
                    {
                        var filter = new BsonDocument { ["_id"] = doc["_id"], ["groupId"] = doc["groupId"] };
                        var existing = await store.Outbox.Find(s, filter).FirstOrDefaultAsync(ct);
                        if (existing is null)
                        {
                            if (!apply) throw new InvalidOperationException($"Thiếu outbox {doc["_id"]}");
                            await store.Outbox.InsertOneAsync(s, doc, cancellationToken: ct);
                        }
                        else if (!JsonNode.DeepEquals(JsonNode.Parse(existing["payload"].AsString), JsonNode.Parse(doc["payload"].AsString))
                            || existing["messageId"] != doc["messageId"] || existing["sequenceNumber"] != doc["sequenceNumber"]
                            || (doc["processedAt"] != BsonNull.Value && existing["processedAt"] == BsonNull.Value))
                            throw new InvalidOperationException($"Outbox bị xung đột: {doc["_id"]}");
                    }
                    return true;
                }, MongoChatStore.TransactionOptions);
            }
            outboxCount += batch.Count;
        }
    }
}
