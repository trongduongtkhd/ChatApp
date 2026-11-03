using System.Security.Cryptography;
using ChatApp.ChatService.Data;
using ChatApp.ChatService.Dtos;
using MongoDB.Bson;
using MongoDB.Driver;

namespace ChatApp.ChatService.Services;

public sealed class AttachmentService(MongoChatStore store, IAttachmentStorage storage)
{
    public static AttachmentDto ToDto(BsonDocument d) => new(Guid.Parse(d["_id"].AsString), d["fileName"].AsString,
        d["contentType"].AsString, d["size"].ToInt64(), d["kind"].AsString);
    public static BsonDocument DtoDocument(AttachmentDto d) => new()
    {
        ["_id"] = d.Id.ToString(), ["fileName"] = d.FileName, ["contentType"] = d.ContentType, ["size"] = d.Size, ["kind"] = d.Kind
    };
    public async Task<BsonDocument> GetAsync(Guid id, Guid group, CancellationToken ct) =>
        await store.Attachments.Find(new BsonDocument { ["_id"] = id.ToString(), ["groupId"] = group.ToString() }).FirstOrDefaultAsync(ct)
        ?? throw new AttachmentException("Không tìm thấy đính kèm.", 404);

    public async Task<UploadStatusDto> StatusAsync(Guid id, Guid group, Guid user, CancellationToken ct)
    {
        var d = await GetAsync(id, group, ct);
        if (d["ownerId"].AsString != user.ToString()) throw new AttachmentException("Không có quyền với upload này.", 403);
        return new(d["state"].AsString, ToDto(d));
    }

    public async Task<AttachmentDto> UploadAsync(Guid id, Guid group, Guid owner, string name, Stream data, CancellationToken ct)
    {
        if (id == Guid.Empty) throw new AttachmentException("Mã upload không hợp lệ.");
        name = AttachmentValidation.CleanName(name);
        var format = await AttachmentValidation.ValidateAsync(data, name, ct);
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(data, ct)); data.Position = 0;
        var now = DateTime.UtcNow;
        var d = new BsonDocument
        {
            ["_id"] = id.ToString(), ["groupId"] = group.ToString(), ["ownerId"] = owner.ToString(),
            ["fileName"] = name, ["contentType"] = format.Type, ["kind"] = format.Kind, ["size"] = data.Length,
            ["sha256"] = hash, ["state"] = "Uploading", ["createdAt"] = now, ["expiresAt"] = now.AddHours(24),
            ["objectKey"] = $"conversations/{group}/{id}/{Guid.NewGuid():N}"
        };
        try { await store.Attachments.InsertOneAsync(d, cancellationToken: ct); }
        catch (MongoWriteException ex) when (ex.WriteError.Code == 11000)
        {
            var existing = await GetAsync(id, group, ct);
            if (existing["ownerId"].AsString != owner.ToString()) throw new AttachmentException("Mã upload đã được sử dụng.", 409);
            if (existing["sha256"].AsString != hash || existing["fileName"].AsString != name)
                throw new AttachmentException("Mã upload không khớp nội dung file.", 409);
            if (existing["state"].AsString is "Ready" or "Attached") return ToDto(existing);
            throw new AttachmentException("Upload chưa sẵn sàng hoặc đã hủy. Hãy kiểm tra trạng thái và thử lại.", 409);
        }
        try
        {
            await storage.PutAsync(d["objectKey"].AsString, data, format.Type, ct);
            var changed = await store.Attachments.UpdateOneAsync(
                new BsonDocument { ["_id"] = id.ToString(), ["state"] = "Uploading", ["expiresAt"] = new BsonDocument("$gt", DateTime.UtcNow) },
                new BsonDocument("$set", new BsonDocument("state", "Ready")), cancellationToken: ct);
            if (changed.ModifiedCount != 1) throw new AttachmentException("Upload đã hết hạn hoặc bị hủy.", 409);
            return ToDto(d);
        }
        catch
        {
            // Không xóa mù khi phản hồi Mongo bị mất: trạng thái Ready có thể đã commit.
            using var recovery = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            try { await store.Attachments.UpdateOneAsync(new BsonDocument { ["_id"] = id.ToString(), ["state"] = "Uploading" },
                new BsonDocument("$set", new BsonDocument("state", "Failed")), cancellationToken: recovery.Token); } catch { /* worker dọn theo hạn */ }
            throw;
        }
    }

    // Cùng transaction với message/counter/Outbox; cleanup cạnh tranh trên chính document này.
    public async Task<AttachmentDto[]> AttachAsync(IClientSessionHandle session, Guid[] ids, Guid group, Guid owner, Guid message, CancellationToken ct)
    {
        var result = new List<AttachmentDto>();
        foreach (var id in ids)
        {
            var d = await store.Attachments.FindOneAndUpdateAsync(session, new BsonDocument
            {
                ["_id"] = id.ToString(), ["groupId"] = group.ToString(), ["ownerId"] = owner.ToString(),
                ["state"] = "Ready", ["expiresAt"] = new BsonDocument("$gt", DateTime.UtcNow)
            }, new BsonDocument("$set", new BsonDocument { ["state"] = "Attached", ["messageId"] = message.ToString() }),
                new FindOneAndUpdateOptions<BsonDocument> { ReturnDocument = ReturnDocument.After }, ct);
            if (d is null) throw new AttachmentException("Đính kèm không sẵn sàng, đã gửi, hết hạn hoặc không thuộc cuộc trò chuyện này.", 409);
            result.Add(ToDto(d));
        }
        if (result.Sum(x => x.Size) > AttachmentValidation.MaxTotal) throw new AttachmentException("Tổng file trong một tin không vượt 50 MiB.", 413);
        return result.ToArray();
    }

    public async Task CancelAsync(Guid id, Guid group, Guid user, CancellationToken ct)
    {
        var d = await GetAsync(id, group, ct);
        if (d["ownerId"].AsString != user.ToString()) throw new AttachmentException("Không có quyền hủy upload.", 403);
        var result = await store.Attachments.UpdateOneAsync(new BsonDocument
        { ["_id"] = id.ToString(), ["state"] = new BsonDocument("$in", new BsonArray { "Ready", "Failed" }) },
            new BsonDocument("$set", new BsonDocument { ["state"] = "Deleting", ["expiresAt"] = DateTime.UtcNow }), cancellationToken: ct);
        if (result.ModifiedCount == 0 && d["state"].AsString is not ("Deleting" or "Deleted"))
            throw new AttachmentException("Không thể hủy file đã gửi hoặc đang upload.", 409);
    }
}
