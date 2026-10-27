using System.ComponentModel.DataAnnotations;

namespace ChatApp.GroupService.Dtos;

public record GroupRoleRequest([Required, RegularExpression("^(Admin|Member)$")] string Role, uint Version);
public record TransferOwnerRequest(Guid UserId, uint Version);
public record ApprovalSettingRequest(bool RequiresApproval, uint Version);
public record GroupVersionRequest(uint Version);
public record JoinDecisionRequest(bool Approve);
public record JoinRequestDto(Guid Id, Guid GroupId, Guid UserId, string? UserName, string? DisplayName,
    Guid RequestedBy, string Status, DateTimeOffset CreatedAt, Guid? ReviewedBy, DateTimeOffset? ReviewedAt);
public record MembershipResult(MemberDto? Member, JoinRequestDto? Request);
