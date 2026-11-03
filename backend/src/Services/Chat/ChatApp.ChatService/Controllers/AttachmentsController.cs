using Amazon.S3;
using ChatApp.ChatService.Services;
using ChatApp.Common.Auth;
using Grpc.Core;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MongoDB.Driver;

namespace ChatApp.ChatService.Controllers;

[ApiController, Authorize]
[Route("api/chat/groups/{groupId:guid}/attachments")]
public sealed class AttachmentsController(AttachmentService attachments, GroupMemberCache members,
    IAttachmentStorage storage, ILogger<AttachmentsController> logger) : ControllerBase
{
    [HttpPut("{id:guid}")]
    [RequestSizeLimit(AttachmentValidation.MaxFile)]
    public Task<IActionResult> Upload(Guid groupId, Guid id, CancellationToken ct) => Guard(groupId, async () =>
    {
        if (Request.ContentLength is null) throw new AttachmentException("Upload cần Content-Length.", 411);
        if (Request.ContentLength is <= 0 or > AttachmentValidation.MaxFile) throw new AttachmentException("File không vượt 25 MiB và không được rỗng.", 413);
        string name;
        try { name = Uri.UnescapeDataString(Request.Headers["X-File-Name"].ToString()); }
        catch (UriFormatException) { throw new AttachmentException("Tên file không hợp lệ."); }
        // Spool có giới hạn ra temp, không giữ toàn bộ file trong RAM; không lưu ở ổ container lâu dài.
        var path = Path.Combine(Path.GetTempPath(), "chatapp-upload-" + Guid.NewGuid().ToString("N"));
        await using var file = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None,
            65536, FileOptions.Asynchronous | FileOptions.DeleteOnClose);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromMinutes(2));
        var buffer = new byte[65536]; long total = 0; int n;
        while ((n = await Request.Body.ReadAsync(buffer, deadline.Token)) > 0)
        {
            total += n;
            if (total > AttachmentValidation.MaxFile) throw new AttachmentException("File không vượt 25 MiB.", 413);
            await file.WriteAsync(buffer.AsMemory(0, n), deadline.Token);
        }
        if (total != Request.ContentLength) throw new AttachmentException("Upload chưa đầy đủ.");
        file.Position = 0;
        var result = await attachments.UploadAsync(id, groupId, User.GetUserId(), name, file, deadline.Token);
        return Ok(result);
    }, ct);

    [HttpGet("{id:guid}")]
    public Task<IActionResult> Status(Guid groupId, Guid id, CancellationToken ct) => Guard(groupId, async () =>
        Ok(await attachments.StatusAsync(id, groupId, User.GetUserId(), ct)), ct);

    [HttpDelete("{id:guid}")]
    public Task<IActionResult> Cancel(Guid groupId, Guid id, CancellationToken ct) => Guard(groupId, async () =>
    { await attachments.CancelAsync(id, groupId, User.GetUserId(), ct); return NoContent(); }, ct);

    [HttpGet("{id:guid}/content")]
    public Task<IActionResult> Content(Guid groupId, Guid id, CancellationToken ct) => Guard(groupId, async () =>
    {
        var d = await attachments.GetAsync(id, groupId, ct);
        if (d["state"].AsString != "Attached") throw new AttachmentException("File chưa được gửi hoặc không còn khả dụng.", 404);
        var obj = await storage.GetAsync(d["objectKey"].AsString, ct);
        HttpContext.Response.RegisterForDispose(obj);
        Response.Headers.CacheControl = "private, no-store";
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        Response.Headers["Content-Security-Policy"] = "default-src 'none'; sandbox";
        return d["kind"].AsString == "image"
            ? File(obj.ResponseStream, d["contentType"].AsString)
            : File(obj.ResponseStream, "application/octet-stream", d["fileName"].AsString);
    }, ct);

    private async Task<IActionResult> Guard(Guid group, Func<Task<IActionResult>> action, CancellationToken ct)
    {
        try
        {
            if (!await members.IsMemberAsync(group, User.GetUserId(), ct))
                return Problem(title: "Bạn không có quyền truy cập cuộc trò chuyện này.", statusCode: 403);
            return await action();
        }
        catch (AttachmentException ex) { return Problem(title: ex.Message, statusCode: ex.Status); }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        { return Problem(title: "Không tìm thấy nội dung file.", statusCode: 404); }
        catch (Exception ex) when (ex is AmazonS3Exception or Amazon.Runtime.AmazonClientException or HttpRequestException or MongoException or RpcException or TimeoutException or OperationCanceledException)
        {
            logger.LogWarning("Đính kèm tạm gián đoạn: {Error}", ex.Message);
            return Problem(title: "Chưa xử lý được đính kèm, vui lòng thử lại.", statusCode: 503);
        }
    }
}
