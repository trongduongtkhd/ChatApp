using System.Diagnostics;
using ChatApp.ChatService.Grpc;
using ChatApp.Contracts.Grpc;
using Grpc.Core;

var builder = WebApplication.CreateBuilder(args);

// gRPC client gọi group-service. Địa chỉ lấy từ cấu hình:
// local http://localhost:5012, Docker http://group-service:5012 (tên container = DNS nội bộ).
// http:// (không TLS) + endpoint chỉ HTTP/2 ở server → client nói thẳng HTTP/2 (h2c).
builder.Services.AddGrpcClient<GroupMembership.GroupMembershipClient>(o =>
    o.Address = new Uri(builder.Configuration["GrpcServices:GroupService"]
        ?? throw new InvalidOperationException("Thiếu cấu hình GrpcServices:GroupService")));
builder.Services.AddScoped<GroupMembershipClient>();

var app = builder.Build();

app.MapGet("/", () => "ChatApp.ChatService is running");

// TẠM (Phần 6): endpoint thử gọi gRPC khi chưa có SignalR hub. Xóa ở Phần 7.
// Chỉ bật ở Development, không qua Gateway (Gateway chỉ chuyển /api/chat và /hubs/chat).
if (app.Environment.IsDevelopment())
{
    app.MapGet("/debug/membership", async (Guid groupId, Guid userId, GroupMembershipClient groups, CancellationToken ct) =>
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var isMember = await groups.IsMemberAsync(groupId, userId, ct);
            var memberIds = await groups.GetMemberIdsAsync(groupId, ct);
            return Results.Ok(new { groupId, userId, isMember, memberIds, elapsedMs = sw.ElapsedMilliseconds });
        }
        catch (RpcException ex)
        {
            // Đổi status gRPC sang HTTP để dễ quan sát: Unavailable → 503, DeadlineExceeded → 504.
            var httpStatus = ex.StatusCode switch
            {
                StatusCode.InvalidArgument => StatusCodes.Status400BadRequest,
                StatusCode.Unavailable => StatusCodes.Status503ServiceUnavailable,
                StatusCode.DeadlineExceeded => StatusCodes.Status504GatewayTimeout,
                _ => StatusCodes.Status500InternalServerError
            };
            return Results.Json(
                new { grpcStatus = ex.StatusCode.ToString(), detail = ex.Status.Detail, elapsedMs = sw.ElapsedMilliseconds },
                statusCode: httpStatus);
        }
    });
}

app.Run();
