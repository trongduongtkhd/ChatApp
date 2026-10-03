using System.ComponentModel.DataAnnotations;

namespace ChatApp.GroupService.Dtos;

public record AddMemberRequest([Required] Guid? UserId);

public record UserSnapshotDto(Guid UserId, string UserName, string DisplayName);
