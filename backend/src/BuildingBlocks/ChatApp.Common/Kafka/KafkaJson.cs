using System.Text.Json;

namespace ChatApp.Common.Kafka;

// Một cách chuyển đổi JSON duy nhất cho mọi message Kafka: tên field dạng camelCase (userId, eventId...),
// đọc không phân biệt hoa thường. Bên phát và bên nghe dùng chung → không lệch định dạng.
public static class KafkaJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
}
