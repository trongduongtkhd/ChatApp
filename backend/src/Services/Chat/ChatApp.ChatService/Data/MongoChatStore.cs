using ChatApp.ChatService.Dtos;
using ChatApp.ChatService.Services;
using MongoDB.Bson;
using MongoDB.Driver;

namespace ChatApp.ChatService.Data;

public sealed class MongoChatStore
{
    public IMongoClient Client { get; }
    public IMongoDatabase Database { get; }
    public IMongoCollection<BsonDocument> Messages => Database.GetCollection<BsonDocument>("messages");
    public IMongoCollection<BsonDocument> Keys => Database.GetCollection<BsonDocument>("message_keys");
    public IMongoCollection<BsonDocument> States => Database.GetCollection<BsonDocument>("conversation_state");
    public IMongoCollection<BsonDocument> Outbox => Database.GetCollection<BsonDocument>("outbox_messages");
    public IMongoCollection<BsonDocument> Attachments => Database.GetCollection<BsonDocument>("attachments");
    public static readonly TransactionOptions TransactionOptions = new(
        readConcern: ReadConcern.Snapshot, writeConcern: WriteConcern.WMajority, readPreference: ReadPreference.Primary);

    public MongoChatStore(IConfiguration configuration)
    {
        var settings = MongoClientSettings.FromConnectionString(configuration["Mongo:ConnectionString"]
            ?? throw new InvalidOperationException("Thiếu Mongo:ConnectionString"));
        settings.ServerSelectionTimeout = TimeSpan.FromSeconds(3);
        settings.ConnectTimeout = TimeSpan.FromSeconds(3);
        settings.SocketTimeout = TimeSpan.FromSeconds(8);
        Client = new MongoClient(settings);
        Database = Client.GetDatabase(configuration["Mongo:Database"] ?? "chat_history");
    }
    public async Task EnsureReadyAsync(CancellationToken ct = default)
    {
        var ready = await Database.GetCollection<BsonDocument>("system_metadata")
            .Find(new BsonDocument("_id", "storage-ready")).FirstOrDefaultAsync(ct);
        if (ready is null || !ready.GetValue("completed", false).AsBoolean)
            throw new InvalidOperationException("Mongo chưa đối chiếu dữ liệu: chạy chat-migrate --migrate-chat --apply trước.");
        // Collection nhỏ quản lý vòng đời upload; _id unique. Không dùng TTL xóa metadata trước nội dung.
        await Attachments.Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(
            new BsonDocument { ["state"] = 1, ["expiresAt"] = 1 }), cancellationToken: ct);
    }
    // GUID dạng chuỗi chuẩn; ticks giữ độ chính xác PostgreSQL, Date phục vụ truy vấn Mongo.
    public static BsonDocument Document(MessageDto m) => new()
    {
        ["_id"] = m.Id.ToString(), ["groupId"] = m.GroupId.ToString(), ["senderId"] = m.SenderId.ToString(),
        ["senderName"] = m.SenderName, ["content"] = m.Content, ["sequenceNumber"] = m.SequenceNumber,
        ["createdAt"] = m.CreatedAt.UtcDateTime, ["createdAtTicks"] = m.CreatedAt.UtcTicks,
        ["weekStartUtc"] = ChatWeek.StartUtc(m.CreatedAt),
        ["attachments"] = new BsonArray((m.Attachments ?? []).Select(AttachmentService.DtoDocument))
    };
    public static MessageDto ToDto(BsonDocument d) => new(Guid.Parse(d["_id"].AsString), Guid.Parse(d["groupId"].AsString),
        Guid.Parse(d["senderId"].AsString), d["senderName"].AsString, d["content"].AsString,
        d["sequenceNumber"].ToInt64(), d.Contains("createdAtTicks")
            ? new DateTimeOffset(d["createdAtTicks"].ToInt64(), TimeSpan.Zero) : new DateTimeOffset(d["createdAt"].ToUniversalTime()),
        d.TryGetValue("attachments", out var a) && a.IsBsonArray ? a.AsBsonArray.Select(x => AttachmentService.ToDto(x.AsBsonDocument)).ToArray() : []);
}
