using ChatApp.ChatService.Data;
using MongoDB.Bson;
using MongoDB.Driver;

namespace ChatApp.ChatService.Services;

public sealed class AttachmentCleanupWorker(MongoChatStore store, IAttachmentStorage storage, ILogger<AttachmentCleanupWorker> logger,
    ChatApp.Common.Backup.BackupWriteGate backupGate) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                for (var i = 0; i < 20; i++)
                {
                    using var checkpointLease = await backupGate.EnterAsync(stoppingToken);
                    var now = DateTime.UtcNow;
                    var filter = new BsonDocument
                    {
                        ["state"] = new BsonDocument("$in", new BsonArray { "Uploading", "Ready", "Failed", "Deleting" }),
                        ["expiresAt"] = new BsonDocument("$lte", now),
                        ["$or"] = new BsonArray { new BsonDocument("cleanupUntil", new BsonDocument("$exists", false)), new BsonDocument("cleanupUntil", new BsonDocument("$lte", now)) }
                    };
                    var d = await store.Attachments.FindOneAndUpdateAsync(filter,
                        new BsonDocument("$set", new BsonDocument { ["state"] = "Deleting", ["cleanupUntil"] = now.AddMinutes(1) }),
                        new FindOneAndUpdateOptions<BsonDocument> { ReturnDocument = ReturnDocument.After }, stoppingToken);
                    if (d is null) break;
                    await storage.DeleteAsync(d["objectKey"].AsString, stoppingToken);
                    await store.Attachments.UpdateOneAsync(new BsonDocument { ["_id"] = d["_id"], ["state"] = "Deleting" },
                        new BsonDocument("$set", new BsonDocument { ["state"] = "Deleted", ["deletedAt"] = DateTime.UtcNow }), cancellationToken: stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogWarning("Chưa dọn được upload bỏ dở: {Error}", ex.Message); }
            try { await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken); } catch (OperationCanceledException) { break; }
        }
    }
}
