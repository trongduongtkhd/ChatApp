namespace ChatApp.ChatService.Dtos;

public sealed record PresenceSnapshotDto(bool Known, long Version, int HeartbeatSeconds, int LeaseSeconds,
    IReadOnlyList<Guid> OnlineUserIds, IReadOnlyList<Guid> FriendIds, IReadOnlyDictionary<Guid, int> OnlineCountsByGroup);
