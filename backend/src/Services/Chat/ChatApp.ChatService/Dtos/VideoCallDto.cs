namespace ChatApp.ChatService.Dtos;

public sealed record VideoCallDto(string Id, string GroupId, string CallerId, string CalleeId,
    string CallerName, string CalleeName, string CallerConnectionId, string CalleeConnectionId,
    string State, string Reason, long Version, long CreatedAt, long Deadline, long ConnectedAt);
public sealed record VideoCallSignalDto(string CallId, string Kind, string Data);
public sealed record VideoIceServer(string[] Urls, string? Username = null, string? Credential = null);
public sealed record VideoCallConfig(string ConnectionId, VideoIceServer[] IceServers, string IceTransportPolicy);
