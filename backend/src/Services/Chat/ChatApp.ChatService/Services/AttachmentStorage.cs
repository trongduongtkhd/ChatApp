using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;

namespace ChatApp.ChatService.Services;

// S3 endpoint là SeaweedFS nội bộ; browser không nhận credentials hoặc object key.
public interface IAttachmentStorage
{
    Task PutAsync(string key, Stream data, string contentType, CancellationToken ct);
    Task<GetObjectResponse> GetAsync(string key, CancellationToken ct);
    Task DeleteAsync(string key, CancellationToken ct);
}

public sealed class S3AttachmentStorage : IAttachmentStorage, IDisposable
{
    private readonly AmazonS3Client client;
    private readonly string bucket;
    public S3AttachmentStorage(IConfiguration config)
    {
        var endpoint = config["Attachments:Endpoint"] ?? throw new InvalidOperationException("Thiếu Attachments:Endpoint");
        bucket = config["Attachments:Bucket"] ?? "chatapp-attachments";
        client = new AmazonS3Client(new BasicAWSCredentials(
            config["AWS_ACCESS_KEY_ID"] ?? throw new InvalidOperationException("Chạy scripts/init-attachment-storage.ps1 trước"),
            config["AWS_SECRET_ACCESS_KEY"] ?? throw new InvalidOperationException("Thiếu khóa kho local")),
            new AmazonS3Config { ServiceURL = endpoint, ForcePathStyle = true, AuthenticationRegion = "us-east-1", MaxErrorRetry = 1 });
    }
    public async Task PutAsync(string key, Stream data, string contentType, CancellationToken ct) =>
        await client.PutObjectAsync(new PutObjectRequest
        {
            BucketName = bucket, Key = key, InputStream = data, ContentType = contentType,
            AutoCloseStream = false, UseChunkEncoding = false
        }, ct);
    public async Task<GetObjectResponse> GetAsync(string key, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(8));
        return await client.GetObjectAsync(new GetObjectRequest { BucketName = bucket, Key = key }, timeout.Token);
    }
    public async Task DeleteAsync(string key, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(8));
        await client.DeleteObjectAsync(bucket, key, timeout.Token);
    }
    public void Dispose() => client.Dispose();
}
