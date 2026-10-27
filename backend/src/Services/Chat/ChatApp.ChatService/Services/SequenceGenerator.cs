using ChatApp.ChatService.Data;
using MongoDB.Bson;
using MongoDB.Driver;

namespace ChatApp.ChatService.Services;

// Cấp số trong cùng transaction với tin/Outbox. Tranh chấp cùng conversation gây write-conflict và retry.
public sealed class SequenceGenerator(MongoChatStore store)
{
    public async Task RaiseFloorAsync(Guid groupId, CancellationToken ct)
    {
        var max = await store.Messages.Find(new BsonDocument("groupId", groupId.ToString()))
            .Sort(new BsonDocument("sequenceNumber", -1)).Limit(1).FirstOrDefaultAsync(ct);
        await store.States.UpdateOneAsync(new BsonDocument("_id", groupId.ToString()),
            Builders<BsonDocument>.Update.Max("lastSeq", max?["sequenceNumber"].ToInt64() ?? 0), cancellationToken: ct);
    }
    public async Task<long> NextAsync(IClientSessionHandle session, Guid groupId, CancellationToken ct)
    {
        var key = new BsonDocument("_id", groupId.ToString());
        var state = await store.States.Find(session, key).FirstOrDefaultAsync(ct);
        if (state is null)
        {
            var max = await store.Messages.Find(session, new BsonDocument("groupId", groupId.ToString()))
                .Sort(new BsonDocument("sequenceNumber", -1)).Limit(1).FirstOrDefaultAsync(ct);
            await store.States.InsertOneAsync(session, new BsonDocument
            {
                ["_id"] = groupId.ToString(), ["lastSeq"] = max?["sequenceNumber"].ToInt64() ?? 0,
                ["hasPending"] = false, ["leaseUntil"] = DateTime.UnixEpoch, ["leaseVersion"] = 0L,
                ["leaseGeneration"] = Guid.NewGuid().ToString()
            }, cancellationToken: ct);
        }
        var after = await store.States.FindOneAndUpdateAsync(session, key,
            Builders<BsonDocument>.Update.Inc("lastSeq", 1L).Set("hasPending", true),
            new FindOneAndUpdateOptions<BsonDocument> { ReturnDocument = ReturnDocument.After }, ct);
        return after["lastSeq"].ToInt64();
    }
}
