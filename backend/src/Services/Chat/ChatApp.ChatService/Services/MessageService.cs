using System.Text.Json;
using ChatApp.ChatService.Data;
using ChatApp.ChatService.Dtos;
using ChatApp.Common.Kafka;
using ChatApp.Contracts.Events;
using MongoDB.Bson;
using MongoDB.Driver;

namespace ChatApp.ChatService.Services;

public enum SendStatus { Created, Duplicate, IdTaken }
public record SendMessageResult(SendStatus Status, MessageDto? Message);

public sealed class MessageService(MongoChatStore store, SequenceGenerator sequence)
{
    public async Task<SendMessageResult> SendAsync(Guid messageId, Guid groupId, Guid senderId,
        string senderName, string content, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        var token = deadline.Token;
        var existing = await ExistingAsync(messageId, groupId, senderId, token);
        if (existing is not null) return existing;
        var createdAt = DateTimeOffset.UtcNow;
        var eventId = Guid.CreateVersion7();
        for (var attempt = 0; ; attempt++)
        {
            using var session = await store.Client.StartSessionAsync(cancellationToken: token);
            try
            {
                return await session.WithTransactionAsync(async (s, transactionCt) =>
                {
                    await store.Keys.InsertOneAsync(s, new BsonDocument
                    {
                        ["_id"] = messageId.ToString(), ["groupId"] = groupId.ToString(), ["senderId"] = senderId.ToString()
                    }, cancellationToken: transactionCt);
                    var seq = await sequence.NextAsync(s, groupId, transactionCt);
                    var message = new MessageDto(messageId, groupId, senderId, senderName, content, seq, createdAt);
                    var preview = content.Length <= 50 ? content : content[..(char.IsHighSurrogate(content[49]) ? 49 : 50)];
                    var evt = new MessageSent(messageId, groupId, senderId, senderName, seq, preview)
                        { EventId = eventId, OccurredAt = createdAt };
                    await store.Messages.InsertOneAsync(s, MongoChatStore.Document(message), cancellationToken: transactionCt);
                    await store.Outbox.InsertOneAsync(s, new BsonDocument
                    {
                        ["_id"] = eventId.ToString(), ["groupId"] = groupId.ToString(), ["messageId"] = messageId.ToString(),
                        ["sequenceNumber"] = seq, ["topic"] = KafkaTopics.ChatMessageSent,
                        ["payload"] = JsonSerializer.Serialize(evt, KafkaJson.Options), ["occurredAt"] = createdAt.UtcDateTime,
                        ["processedAt"] = BsonNull.Value, ["attempts"] = 0, ["lastError"] = BsonNull.Value
                    }, cancellationToken: transactionCt);
                    return new SendMessageResult(SendStatus.Created, message);
                }, MongoChatStore.TransactionOptions, token);
            }
            catch (Exception ex) when (IsDuplicate(ex) && attempt < 20)
            {
                // Khóa messageId trùng hoặc lần đầu cùng tạo state: transaction đã abort, tra nguồn rồi thử lại.
                existing = await ExistingAsync(messageId, groupId, senderId, token);
                if (existing is not null) return existing;
                await sequence.RaiseFloorAsync(groupId, token);
                await Task.Delay(Random.Shared.Next(10, 60), token);
            }
        }
    }

    public static bool IsDuplicate(Exception ex) => ex is MongoWriteException { WriteError.Code: 11000 }
        || ex is MongoCommandException { Code: 11000 };

    private async Task<SendMessageResult?> ExistingAsync(Guid id, Guid group, Guid sender, CancellationToken ct)
    {
        // Snapshot xuyên shard: key và message phải được đọc tại cùng một thời điểm.
        using var session = await store.Client.StartSessionAsync(cancellationToken: ct);
        return await session.WithTransactionAsync(async (s, txCt) =>
        {
            var key = await store.Keys.Find(s, new BsonDocument("_id", id.ToString())).FirstOrDefaultAsync(txCt);
            if (key is null) return null;
            if (key["groupId"].AsString != group.ToString() || key["senderId"].AsString != sender.ToString())
                return new SendMessageResult(SendStatus.IdTaken, null);
            var doc = await store.Messages.Find(s, new BsonDocument { ["_id"] = id.ToString(), ["groupId"] = group.ToString() }).FirstOrDefaultAsync(txCt);
            if (doc is null) throw new InvalidOperationException("Khóa tin không khớp dữ liệu; cần đối chiếu migration.");
            return new SendMessageResult(SendStatus.Duplicate, MongoChatStore.ToDto(doc));
        }, MongoChatStore.TransactionOptions, ct);
    }

    public async Task<IReadOnlyList<MessageDto>> GetHistoryAsync(Guid groupId, long? beforeSeq, int limit,
        CancellationToken ct, DateTime? weekStartUtc = null)
    {
        var filter = new BsonDocument("groupId", groupId.ToString());
        if (beforeSeq is not null) filter["sequenceNumber"] = new BsonDocument("$lt", beforeSeq.Value);
        if (weekStartUtc is not null) filter["weekStartUtc"] = weekStartUtc.Value;
        var docs = await store.Messages.Find(filter, new FindOptions { MaxTime = TimeSpan.FromSeconds(4) })
            .Sort(new BsonDocument("sequenceNumber", -1)).Limit(limit).ToListAsync(ct);
        return docs.AsEnumerable().Reverse().Select(MongoChatStore.ToDto).ToArray();
    }
}
