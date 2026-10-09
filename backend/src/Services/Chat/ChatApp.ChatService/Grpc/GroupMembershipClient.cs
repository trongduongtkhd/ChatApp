using ChatApp.Contracts.Grpc;

namespace ChatApp.ChatService.Grpc;

// Lớp bọc gRPC client do Grpc.Tools sinh (GroupMembership.GroupMembershipClient):
// - Đổi Guid ↔ string (Protobuf không có kiểu GUID) để code nghiệp vụ chỉ thấy Guid.
// - Đặt deadline cho mọi lời gọi: group-service treo thì không chờ mãi.
// Phần 7 (hub gửi tin) gọi lớp này; Phần 12 gắn Polly (retry, circuit breaker) vào đây.
public class GroupMembershipClient(GroupMembership.GroupMembershipClient client, IConfiguration configuration)
{
    private readonly TimeSpan _deadline = TimeSpan.FromSeconds(configuration.GetValue("GrpcServices:DeadlineSeconds", 3));

    // Gọi qua mạng tới group-service (port 5012). Lỗi → RpcException với StatusCode:
    // Unavailable (group-service không chạy), DeadlineExceeded (quá hạn), InvalidArgument (GUID sai)...
    public async Task<bool> IsMemberAsync(Guid groupId, Guid userId, CancellationToken ct = default)
    {
        var reply = await client.CheckMembershipAsync(
            new CheckMembershipRequest { GroupId = groupId.ToString(), UserId = userId.ToString() },
            deadline: DateTime.UtcNow.Add(_deadline),
            cancellationToken: ct);

        return reply.IsMember;
    }

    public async Task<IReadOnlyList<Guid>> GetMemberIdsAsync(Guid groupId, CancellationToken ct = default)
    {
        var reply = await client.GetMemberIdsAsync(
            new GetMemberIdsRequest { GroupId = groupId.ToString() },
            deadline: DateTime.UtcNow.Add(_deadline),
            cancellationToken: ct);

        return reply.UserIds.Select(Guid.Parse).ToList();
    }
}
