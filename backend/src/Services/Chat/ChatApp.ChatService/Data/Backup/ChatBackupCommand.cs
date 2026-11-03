using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using MongoDB.Bson;
using MongoDB.Bson.IO;
using MongoDB.Driver;

namespace ChatApp.ChatService.Data.Backup;

// Administrative CLI in the EXISTING Chat image. Snapshot reads use the SAME
// MongoDB cluster timestamp for every collection, including cross-shard writes.
// Never use mongodump on a live sharded cluster and assume cross-store consistency.
public static class ChatBackupCommand
{
    private static readonly string[] Collections = ["messages", "message_keys", "conversation_state", "outbox_messages", "system_metadata", "attachments"];
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
    private static readonly JsonWriterSettings BsonJson = new() { OutputMode = JsonOutputMode.CanonicalExtendedJson };
    public record CollectionFile(string Name, string File, long Count, string Sha256, string[] Indexes);
    public record ObjectFile(string Id, string Key, string File, long Size, string Sha256, string ContentType);
    public record Catalog(int Version, uint Seconds, uint Increment, CollectionFile[] Collections, ObjectFile[] Objects);

    public static async Task RunAsync(IConfiguration config, string[] args)
    {
        string Arg(string key) { var i = Array.IndexOf(args, key); return i >= 0 && i + 1 < args.Length ? args[i + 1] : throw new ArgumentException("Missing " + key); }
        var mode = Arg("--backup-data");
        var directory = Path.GetFullPath(Arg("--directory"));
        Directory.CreateDirectory(directory);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(30));
        var ct = timeout.Token;
        var client = new MongoClient(config["Mongo:ConnectionString"] ?? throw new InvalidOperationException("Missing Mongo connection"));
        var db = client.GetDatabase(config["Mongo:Database"] ?? "chat_history");
        if (mode == "checkpoint")
        {
            var result = await db.RunCommandAsync<BsonDocument>(new BsonDocument
            {
                ["insert"] = "backup_checkpoints", ["documents"] = new BsonArray { new BsonDocument { ["_id"] = Arg("--id"), ["at"] = DateTime.UtcNow } },
                ["writeConcern"] = new BsonDocument("w", "majority")
            }, cancellationToken: ct);
            var time = result["operationTime"].AsBsonTimestamp;
            await File.WriteAllTextAsync(Path.Combine(directory, "mongo-point.json"),
                JsonSerializer.Serialize(new { seconds = (uint)time.Timestamp, increment = (uint)time.Increment }, Json), ct);
            Console.WriteLine("Mongo checkpoint captured.");
            return;
        }
        if (mode == "export")
        {
            var time = new BsonTimestamp(unchecked((int)uint.Parse(Arg("--seconds"))), unchecked((int)uint.Parse(Arg("--increment"))));
            using var session = await client.StartSessionAsync(new ClientSessionOptions { CausalConsistency = false }, ct);
            var available = await (await db.ListCollectionNamesAsync(cancellationToken: ct)).ToListAsync(ct);
            var unexpected = available.Except(Collections).Except(["backup_checkpoints"]).ToArray();
            if (unexpected.Length > 0) throw new InvalidOperationException("Unknown application collections: " + string.Join(",", unexpected));
            var catalogFiles = new List<CollectionFile>();
            var objects = new List<ObjectFile>();
            using var s3 = CreateS3(config);
            Directory.CreateDirectory(Path.Combine(directory, "objects"));
            foreach (var name in Collections)
            {
                var file = name + ".jsonl";
                long count = 0;
                await using (var writer = new StreamWriter(Path.Combine(directory, file), false, new UTF8Encoding(false)))
                {
                    var filter = name == "attachments" ? new BsonDocument("state", "Attached") : new BsonDocument();
                    await foreach (var document in SnapshotAsync(db, session, name, time, filter, ct))
                    {
                        await writer.WriteLineAsync(document.ToJson(BsonJson).AsMemory(), ct);
                        count++;
                        if (name == "attachments")
                        {
                            var id = document["_id"].AsString;
                            if (!Guid.TryParse(id, out _)) throw new InvalidOperationException("Invalid attachment ID");
                            var objectFile = "objects/" + id + ".bin";
                            using var response = await s3.GetObjectAsync(config["Attachments:Bucket"] ?? "chatapp-attachments", document["objectKey"].AsString, ct);
                            await using (var output = File.Create(Path.Combine(directory, objectFile)))
                                await response.ResponseStream.CopyToAsync(output, ct);
                            var hash = await HashAsync(Path.Combine(directory, objectFile), ct);
                            var length = new FileInfo(Path.Combine(directory, objectFile)).Length;
                            if (!hash.Equals(document["sha256"].AsString, StringComparison.OrdinalIgnoreCase) || length != document["size"].ToInt64())
                                throw new InvalidOperationException("Attachment content mismatch: " + id);
                            objects.Add(new(id, document["objectKey"].AsString, objectFile, length, hash, document["contentType"].AsString));
                        }
                    }
                }
                var indexes = available.Contains(name)
                    ? (await (await db.GetCollection<BsonDocument>(name).Indexes.ListAsync(ct)).ToListAsync(ct)).Select(x => x.ToJson(BsonJson)).ToArray() : [];
                catalogFiles.Add(new(name, file, count, await HashAsync(Path.Combine(directory, file), ct), indexes));
            }
            var catalog = new Catalog(1, (uint)time.Timestamp, (uint)time.Increment, catalogFiles.ToArray(), objects.ToArray());
            await File.WriteAllTextAsync(Path.Combine(directory, "catalog.json"), JsonSerializer.Serialize(catalog, Json), ct);
            Console.WriteLine($"Mongo snapshot exported: {catalogFiles.Sum(x => x.Count)} documents, {objects.Count} objects.");
            return;
        }
        if (mode != "import") throw new ArgumentException("Unsupported backup-data mode");
        if (!config.GetValue("Backup:RecoveryMode", false)) throw new InvalidOperationException("Import is only enabled in an isolated recovery container.");
        var catalogIn = JsonSerializer.Deserialize<Catalog>(await File.ReadAllTextAsync(Path.Combine(directory, "catalog.json"), ct), Json)
            ?? throw new InvalidOperationException("Missing catalog");
        if (catalogIn.Version != 1 || catalogIn.Collections.Select(x => x.Name).Except(Collections).Any()) throw new InvalidOperationException("Unsupported catalog");
        if (await db.GetCollection<BsonDocument>("messages").CountDocumentsAsync(new BsonDocument(), cancellationToken: ct) != 0)
            throw new InvalidOperationException("Recovery target is not empty; import refused.");
        using var targetS3 = CreateS3(config);
        var bucket = config["Attachments:Bucket"] ?? "chatapp-attachments";
        try { await targetS3.PutBucketAsync(new PutBucketRequest { BucketName = bucket }, ct); }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.Conflict) { }
        foreach (var item in catalogIn.Objects)
        {
            var path = SafeFile(directory, item.File);
            if (new FileInfo(path).Length != item.Size || !item.Sha256.Equals(await HashAsync(path, ct), StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Object checksum mismatch");
            await using var input = File.OpenRead(path);
            await targetS3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = item.Key, InputStream = input, ContentType = item.ContentType, UseChunkEncoding = false, AutoCloseStream = false }, ct);
        }
        foreach (var file in catalogIn.Collections)
        {
            var path = SafeFile(directory, file.File);
            if (!file.Sha256.Equals(await HashAsync(path, ct), StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Mongo file checksum mismatch");
            var collection = db.GetCollection<BsonDocument>(file.Name);
            await collection.DeleteManyAsync(new BsonDocument(), ct);
            using var reader = File.OpenText(path);
            var batch = new List<BsonDocument>(128);
            long count = 0;
            while (await reader.ReadLineAsync(ct) is { } line)
            {
                batch.Add(BsonDocument.Parse(line)); count++;
                if (batch.Count == 128) { await collection.InsertManyAsync(batch, cancellationToken: ct); batch.Clear(); }
            }
            if (batch.Count > 0) await collection.InsertManyAsync(batch, cancellationToken: ct);
            if (count != file.Count) throw new InvalidOperationException("Mongo document count mismatch");
            foreach (var text in file.Indexes)
            {
                var index = BsonDocument.Parse(text);
                if (index["name"] == "_id_") continue;
                index.Remove("v"); index.Remove("ns"); index.Remove("background");
                await db.RunCommandAsync<BsonDocument>(new BsonDocument { ["createIndexes"] = file.Name, ["indexes"] = new BsonArray { index } }, cancellationToken: ct);
            }
        }
        // Publisher identities belong to the old runtime, not to recovered users.
        await db.GetCollection<BsonDocument>("conversation_state").UpdateManyAsync(new BsonDocument(),
            new BsonDocument("$set", new BsonDocument("leaseUntil", DateTime.UnixEpoch)), cancellationToken: ct);
        Console.WriteLine($"Imported {catalogIn.Collections.Sum(x => x.Count)} documents and {catalogIn.Objects.Length} verified objects.");
    }

    private static async IAsyncEnumerable<BsonDocument> SnapshotAsync(IMongoDatabase db, IClientSessionHandle session,
        string collection, BsonTimestamp time, BsonDocument filter, [EnumeratorCancellation] CancellationToken ct)
    {
        var response = await db.RunCommandAsync<BsonDocument>(session, new BsonDocument
        {
            ["find"] = collection, ["filter"] = filter, ["sort"] = new BsonDocument("_id", 1), ["batchSize"] = 128,
            ["readConcern"] = new BsonDocument { ["level"] = "snapshot", ["atClusterTime"] = time }, ["maxTimeMS"] = 30000
        }, cancellationToken: ct);
        while (true)
        {
            var cursor = response["cursor"].AsBsonDocument;
            foreach (var item in cursor.GetValue("firstBatch", cursor.GetValue("nextBatch", new BsonArray())).AsBsonArray) yield return item.AsBsonDocument;
            if (cursor["id"].ToInt64() == 0) break;
            response = await db.RunCommandAsync<BsonDocument>(session, new BsonDocument
                { ["getMore"] = cursor["id"], ["collection"] = collection, ["batchSize"] = 128 }, cancellationToken: ct);
        }
    }
    private static AmazonS3Client CreateS3(IConfiguration config) => new(new BasicAWSCredentials(config["AWS_ACCESS_KEY_ID"], config["AWS_SECRET_ACCESS_KEY"]),
        new AmazonS3Config { ServiceURL = config["Attachments:Endpoint"], ForcePathStyle = true, AuthenticationRegion = "us-east-1", MaxErrorRetry = 1 });
    private static async Task<string> HashAsync(string path, CancellationToken ct)
    { await using var stream = File.OpenRead(path); return Convert.ToHexString(await SHA256.HashDataAsync(stream, ct)); }
    private static string SafeFile(string root, string file)
    {
        var path = Path.GetFullPath(Path.Combine(root, file));
        if (!path.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.Ordinal)) throw new InvalidOperationException("Unsafe catalog file");
        return path;
    }
}
