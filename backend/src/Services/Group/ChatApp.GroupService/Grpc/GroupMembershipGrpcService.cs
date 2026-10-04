using ChatApp.Contracts.Grpc;
using ChatApp.GroupService.Data;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;

namespace ChatApp.GroupService.Grpc;

// gRPC server (port 5012, HTTP/2), chỉ cho service nội bộ gọi (chat-service), không đi qua Gateway.
// Kế thừa GroupMembershipBase do Grpc.Tools sinh từ group_membership.proto: chỉ cần override từng rpc.
// Mỗi lời gọi gRPC tạo một scope DI riêng (giống request HTTP) → nhận GroupDbContext qua constructor được.
public class GroupMembershipGrpcService(GroupDbContext db) : GroupMembership.GroupMembershipBase
{
    public override async Task<CheckMembershipReply> CheckMembership(CheckMembershipRequest request, ServerCallContext context)
    {
        var groupId = ParseGuid(request.GroupId, "group_id");
        var userId = ParseGuid(request.UserId, "user_id");

        // context.CancellationToken: client hủy hoặc hết deadline → truy vấn DB cũng dừng theo.
        var isMember = await db.GroupMembers
            .AsNoTracking()
            .AnyAsync(m => m.GroupId == groupId && m.UserId == userId, context.CancellationToken);

        return new CheckMembershipReply { IsMember = isMember };
    }

    public override async Task<GetMemberIdsReply> GetMemberIds(GetMemberIdsRequest request, ServerCallContext context)
    {
        var groupId = ParseGuid(request.GroupId, "group_id");

        var memberIds = await db.GroupMembers
            .AsNoTracking()
            .Where(m => m.GroupId == groupId)
            .Select(m => m.UserId)
            .ToListAsync(context.CancellationToken);

        // Nhóm không tồn tại → danh sách rỗng (không phải lỗi): bên gọi hiểu là "không ai là thành viên".
        var reply = new GetMemberIdsReply();
        reply.UserIds.AddRange(memberIds.Select(id => id.ToString()));
        return reply;
    }

    // Protobuf không có kiểu GUID nên truyền chuỗi; chuỗi sai → status InvalidArgument (tương đương HTTP 400).
    private static Guid ParseGuid(string value, string field) =>
        Guid.TryParse(value, out var id)
            ? id
            : throw new RpcException(new Status(StatusCode.InvalidArgument, $"{field} không phải GUID hợp lệ: '{value}'"));
}
