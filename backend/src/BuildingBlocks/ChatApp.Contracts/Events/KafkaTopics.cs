namespace ChatApp.Contracts.Events;

// Tên topic theo DESIGN mục 4. Bên phát và bên nghe cùng dùng hằng số này → không gõ sai tên.
// Số partition được tạo cố định trong container kafka-init (docker-compose.yml).
public static class KafkaTopics
{
    public const string UserRegistered = "identity.user-registered";
    public const string MemberAdded = "group.member-added";
    public const string MemberRemoved = "group.member-removed";
    public const string ChatMessageSent = "chat.message-sent";
}
