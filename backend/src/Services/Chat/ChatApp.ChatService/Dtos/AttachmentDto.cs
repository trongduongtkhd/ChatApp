namespace ChatApp.ChatService.Dtos;

public sealed record AttachmentDto(Guid Id, string FileName, string ContentType, long Size, string Kind);
public sealed record UploadStatusDto(string State, AttachmentDto Attachment);
