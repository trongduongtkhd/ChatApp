using ChatApp.Common.Auth;
using ChatApp.Common.Kafka;
using ChatApp.NotificationService.Data;
using ChatApp.NotificationService.Hubs;
using ChatApp.NotificationService.Messaging;
using ChatApp.NotificationService.Services;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// Connection string lấy từ cấu hình: user-secrets khi chạy local, biến môi trường khi chạy Docker.
builder.Services.AddDbContext<NotificationDbContext>(options =>
    options
        .UseNpgsql(builder.Configuration.GetConnectionString("NotificationDb"))
        .UseSnakeCaseNamingConvention());

// Cùng code kiểm tra JWT với Gateway và các service khác (ChatApp.Common), cùng secret trong kho chung.
// Hub /hubs/notifications (Bước 5) nhận token qua ?access_token= nhờ OnMessageReceived trong Common.
builder.Services.AddChatAppJwtAuthentication(builder.Configuration);

// Dùng DbContext (Scoped) → Scoped: mỗi sự kiện Kafka một scope riêng (KafkaConsumerBase tự tạo).
builder.Services.AddScoped<MemberSnapshotService>();
builder.Services.AddScoped<UnreadCounterService>();

// Consumer Kafka chạy nền (consumer group "notification-service"):
// giữ bản sao thành viên nhóm + tăng số tin chưa đọc khi có tin mới.
builder.Services.AddChatAppKafkaConsumer<MemberAddedConsumer>(builder.Configuration);
builder.Services.AddChatAppKafkaConsumer<MemberRemovedConsumer>(builder.Configuration);
builder.Services.AddChatAppKafkaConsumer<MessageSentConsumer>(builder.Configuration);

// REST /api/notifications/* (Controllers/NotificationsController).
builder.Services.AddControllers();

// SignalR /hubs/notifications. Chạy 1 bản nên chưa cần Redis Backplane: Clients.User(...) chỉ cần tới các kết nối
// nằm trên chính bản này. Chạy 2 bản thì phải thêm backplane như chat-service (Phần 8).
builder.Services.AddSignalR();
// Định danh user của SignalR = claim "sub" (để Clients.User(userId) tìm đúng kết nối).
builder.Services.AddSingleton<IUserIdProvider, SubUserIdProvider>();
// Chỉ giữ IHubContext (Singleton) → Singleton được.
builder.Services.AddSingleton<UnreadNotifier>();

var app = builder.Build();

// Tự áp migration khi khởi động.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<NotificationDbContext>();
    await db.Database.MigrateAsync();
}

// Công tắc demo (Bước 6): in rõ lúc khởi động để không quên bật lại.
if (app.Configuration.GetValue("Notification:IdempotentConsumer", true))
    app.Logger.LogInformation("Idempotent Consumer: BẬT (processed_events chặn sự kiện trùng)");
else
    app.Logger.LogWarning("Idempotent Consumer: TẮT → sự kiện trùng sẽ bị ĐẾM LẠI (chỉ dùng để demo)");

app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/", () => "ChatApp.NotificationService is running");
app.MapControllers();

// Client kết nối ws://.../hubs/notifications (qua Gateway), JWT qua ?access_token= (Common).
app.MapHub<NotificationHub>("/hubs/notifications");

app.Run();
