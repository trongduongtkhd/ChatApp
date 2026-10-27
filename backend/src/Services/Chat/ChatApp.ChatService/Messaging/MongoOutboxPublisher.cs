using ChatApp.ChatService.Data;
using ChatApp.Common.Kafka;
using MongoDB.Bson;
using MongoDB.Driver;

namespace ChatApp.ChatService.Messaging;

// Lease theo conversation, gửi tuần tự. ACK chỉ cập nhật nếu owner/version vẫn hợp lệ.
// Worker cũ bị fencing không được chuyển sang tin kế tiếp; gửi trùng giữ nguyên eventId.
public sealed class MongoOutboxPublisher(MongoChatStore store, IKafkaProducer producer,
    IConfiguration configuration, ILogger<MongoOutboxPublisher> logger) : BackgroundService
{
    private readonly string owner = Guid.NewGuid().ToString();
    private readonly int leaseMs = configuration.GetValue("MongoOutbox:LeaseSeconds", 30) * 1000;
    private static BsonDocument Expired => new("$expr", new BsonDocument("$lte", new BsonArray
        { new BsonDocument("$ifNull", new BsonArray { "$leaseUntil", DateTime.UnixEpoch }), "$$NOW" }));
    private static BsonDocument Alive => new("$expr", new BsonDocument("$gt", new BsonArray { "$leaseUntil", "$$NOW" }));
    private UpdateDefinition<BsonDocument> Renew(bool acquire = false) => new PipelineUpdateDefinition<BsonDocument>(new[]
    {
        new BsonDocument("$set", new BsonDocument
        {
            ["leaseUntil"] = new BsonDocument("$add", new BsonArray { "$$NOW", leaseMs }),
            ["leaseOwner"] = owner,
            ["leaseGeneration"] = new BsonDocument("$ifNull", new BsonArray { "$leaseGeneration", Guid.NewGuid().ToString() }),
            ["leaseVersion"] = acquire ? new BsonDocument("$add", new BsonArray
                { new BsonDocument("$ifNull", new BsonArray { "$leaseVersion", 0L }), 1L }) : "$leaseVersion"
        })
    });

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var candidates = await store.States.Find(Builders<BsonDocument>.Filter.And(
                    new BsonDocument("hasPending", true), Expired)).Limit(32).ToListAsync(stoppingToken);
                await Parallel.ForEachAsync(candidates, new ParallelOptions { CancellationToken = stoppingToken, MaxDegreeOfParallelism = 4 },
                    async (candidate, ct) => await PublishAsync(candidate["_id"].AsString, ct));
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            { logger.LogWarning("Mongo Outbox chờ phục hồi: {Error}", ex.Message); }
            try { await Task.Delay(500, stoppingToken); } catch (OperationCanceledException) { break; }
        }
    }

    private async Task PublishAsync(string groupId, CancellationToken ct)
    {
        var claim = await store.States.FindOneAndUpdateAsync(Builders<BsonDocument>.Filter.And(
            new BsonDocument("_id", groupId), new BsonDocument("hasPending", true), Expired), Renew(true),
            new FindOneAndUpdateOptions<BsonDocument> { ReturnDocument = ReturnDocument.After }, ct);
        if (claim is null) return;
        var version = claim["leaseVersion"].ToInt64();
        var generation = claim["leaseGeneration"].AsString;
        var ownership = Builders<BsonDocument>.Filter.And(new BsonDocument
            { ["_id"] = groupId, ["leaseOwner"] = owner, ["leaseVersion"] = version, ["leaseGeneration"] = generation }, Alive);
        try
        {
            for (var i = 0; i < 100 && !ct.IsCancellationRequested; i++)
            {
                // Mỗi lần chỉ một tin đang chờ Kafka ACK. Không giữ transaction Mongo khi gọi Kafka.
                var renewed = await store.States.UpdateOneAsync(ownership, Renew(), cancellationToken: ct);
                if (renewed.MatchedCount != 1) return;
                var pending = await store.Outbox.Find(new BsonDocument { ["groupId"] = groupId, ["processedAt"] = BsonNull.Value })
                    .Sort(new BsonDocument("sequenceNumber", 1)).Limit(1).FirstOrDefaultAsync(ct);
                if (pending is null)
                {
                    // Chỉ clear hasPending trong transaction chạm cùng state với các writer.
                    // Tin mới chen vào gây write conflict, callback retry sẽ thấy pending mới.
                    using var emptySession = await store.Client.StartSessionAsync(cancellationToken: ct);
                    await emptySession.WithTransactionAsync(async (s, txCt) =>
                    {
                        if ((await store.States.UpdateOneAsync(s, ownership, Renew(), cancellationToken: txCt)).MatchedCount != 1) return false;
                        var next = await store.Outbox.Find(s, new BsonDocument { ["groupId"] = groupId, ["processedAt"] = BsonNull.Value }).Limit(1).FirstOrDefaultAsync(txCt);
                        if (next is null) await store.States.UpdateOneAsync(s, ownership,
                            Builders<BsonDocument>.Update.Set("hasPending", false), cancellationToken: txCt);
                        return true;
                    }, MongoChatStore.TransactionOptions, ct);
                    return;
                }
                var filter = new BsonDocument { ["_id"] = pending["_id"], ["groupId"] = groupId, ["processedAt"] = BsonNull.Value };
                await store.Outbox.UpdateOneAsync(filter, Builders<BsonDocument>.Update.Inc("attempts", 1), cancellationToken: ct);
                try { await producer.ProduceAsync(pending["topic"].AsString, groupId, pending["payload"].AsString, ct); }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    await store.Outbox.UpdateOneAsync(filter, Builders<BsonDocument>.Update.Set("lastError", ex.Message[..Math.Min(1000, ex.Message.Length)]), cancellationToken: ct);
                    return; // không vượt qua tin gửi lỗi
                }
                using var ackSession = await store.Client.StartSessionAsync(cancellationToken: ct);
                var marked = await ackSession.WithTransactionAsync(async (s, txCt) =>
                {
                    if ((await store.States.UpdateOneAsync(s, ownership, Renew(), cancellationToken: txCt)).MatchedCount != 1) return false;
                    await store.Outbox.UpdateOneAsync(s, filter,
                        Builders<BsonDocument>.Update.Set("processedAt", DateTime.UtcNow).Set("lastError", BsonNull.Value), cancellationToken: txCt);
                    return true;
                }, MongoChatStore.TransactionOptions, ct);
                if (!marked) return;
            }
        }
        finally
        {
            // Chỉ owner/version này được trả lease; không đụng lease của worker mới.
            try { await store.States.UpdateOneAsync(new BsonDocument { ["_id"] = groupId, ["leaseOwner"] = owner, ["leaseVersion"] = version, ["leaseGeneration"] = generation },
                Builders<BsonDocument>.Update.Set("leaseUntil", DateTime.UnixEpoch), cancellationToken: CancellationToken.None); }
            catch (Exception ex) { logger.LogDebug(ex, "Không trả lease được; sẽ tự hết hạn"); }
        }
    }
}
