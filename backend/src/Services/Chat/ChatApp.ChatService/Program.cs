using ChatApp.ChatService.Data;
using ChatApp.ChatService.Grpc;
using ChatApp.ChatService.Hubs;
using ChatApp.ChatService.Messaging;
using ChatApp.ChatService.Services;
using ChatApp.Common.Auth;
using ChatApp.Common.Kafka;
using ChatApp.Common.Outbox;
using ChatApp.Contracts.Grpc;
using Microsoft.EntityFrameworkCore;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);

// Connection string lấy từ cấu hình: user-secrets khi chạy local, biến môi trường khi chạy Docker.
builder.Services.AddDbContext<ChatDbContext>(options =>
    options
        .UseNpgsql(builder.Configuration.GetConnectionString("ChatDb"))
        .UseSnakeCaseNamingConvention());

// Cùng code kiểm tra JWT với Gateway và các service khác (ChatApp.Common), cùng secret trong kho chung.
// Hub SignalR (Bước 2) nhận token qua ?access_token= nhờ OnMessageReceived trong Common.
builder.Services.AddChatAppJwtAuthentication(builder.Configuration);

// Kafka producer + OutboxPublisher (BackgroundService) đẩy outbox_messages của chat_db lên Kafka.
builder.Services.AddChatAppOutbox<ChatDbContext>(builder.Configuration);

// Redis: MỘT kết nối dùng chung cho cả service (Singleton). ConnectionMultiplexer an toàn đa luồng,
// tự ghép nhiều lệnh từ nhiều request lên cùng một kết nối TCP → không mở kết nối mới mỗi request.
// AbortOnConnectFail = false: Redis chưa sẵn sàng thì service VẪN khởi động, thư viện tự kết nối lại ở nền.
builder.Services.AddSingleton<IConnectionMultiplexer>(_ =>
{
    var options = ConfigurationOptions.Parse(builder.Configuration.GetConnectionString("Redis")
        ?? throw new InvalidOperationException("Thiếu cấu hình ConnectionStrings:Redis"));
    options.AbortOnConnectFail = false;
    return ConnectionMultiplexer.Connect(options);
});

// gRPC client gọi group-service. Địa chỉ lấy từ cấu hình:
// local http://localhost:5012, Docker http://group-service:5012 (tên container = DNS nội bộ).
// http:// (không TLS) + endpoint chỉ HTTP/2 ở server → client nói thẳng HTTP/2 (h2c).
builder.Services.AddGrpcClient<GroupMembership.GroupMembershipClient>(o =>
    o.Address = new Uri(builder.Configuration["GrpcServices:GroupService"]
        ?? throw new InvalidOperationException("Thiếu cấu hình GrpcServices:GroupService")));
builder.Services.AddScoped<GroupMembershipClient>();

// REST lịch sử tin nhắn (Controllers/MessagesController).
builder.Services.AddControllers();

// SignalR (có sẵn trong ASP.NET Core, không cần package). Chạy 1 bản nên chưa cần Redis Backplane (Phần 8).
builder.Services.AddSignalR();

// SequenceGenerator, MessageService dùng DbContext (Scoped: mỗi lần gọi hub một scope) → Scoped.
builder.Services.AddScoped<SequenceGenerator>();
builder.Services.AddScoped<MessageService>();
builder.Services.AddScoped<GroupMemberCache>();
// Chỉ giữ IConnectionMultiplexer (Singleton) → Singleton được.
builder.Services.AddSingleton<PresenceTracker>();

// Consumer Kafka chạy nền (consumer group "chat-service"): thành viên thay đổi → xóa cache group:members:{groupId}.
builder.Services.AddChatAppKafkaConsumer<MemberAddedConsumer>(builder.Configuration);
builder.Services.AddChatAppKafkaConsumer<MemberRemovedConsumer>(builder.Configuration);

var app = builder.Build();

// Tự áp migration khi khởi động.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<ChatDbContext>();
    await db.Database.MigrateAsync();
}

// Mở kết nối Redis ngay lúc khởi động (thay vì đợi request đầu tiên) để thấy trạng thái trong log.
var redis = app.Services.GetRequiredService<IConnectionMultiplexer>();
app.Logger.LogInformation("Redis {Endpoints}: {State}",
    string.Join(", ", redis.GetEndPoints().Select(e => e.ToString())),
    redis.IsConnected ? "đã kết nối" : "CHƯA kết nối, sẽ tự thử lại");

app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/", () => "ChatApp.ChatService is running");
app.MapControllers();

// Hub SignalR: client kết nối ws://.../hubs/chat (qua Gateway). Thay cho endpoint tạm /debug/membership của Phần 6.
app.MapHub<ChatHub>("/hubs/chat");

app.Run();
