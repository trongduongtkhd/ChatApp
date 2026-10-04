using ChatApp.Common.Auth;
using ChatApp.Common.Kafka;
using ChatApp.Common.Outbox;
using ChatApp.GroupService.Data;
using ChatApp.GroupService.Grpc;
using ChatApp.GroupService.Messaging;
using ChatApp.GroupService.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi;

var builder = WebApplication.CreateBuilder(args);

// Connection string lấy từ cấu hình: user-secrets khi chạy local, biến môi trường khi chạy Docker.
builder.Services.AddDbContext<GroupDbContext>(options =>
    options
        .UseNpgsql(builder.Configuration.GetConnectionString("GroupDb"))
        .UseSnakeCaseNamingConvention());

// Cùng code kiểm tra JWT với Gateway và identity (ChatApp.Common), cùng secret trong kho chung.
builder.Services.AddChatAppJwtAuthentication(builder.Configuration);

// Kafka producer + OutboxPublisher (BackgroundService) đẩy outbox_messages của group_db lên Kafka.
builder.Services.AddChatAppOutbox<GroupDbContext>(builder.Configuration);

// Consumer Kafka chạy nền: identity.user-registered → user_snapshots.
builder.Services.AddChatAppKafkaConsumer<UserRegisteredConsumer>(builder.Configuration);

builder.Services.AddScoped<GroupManagementService>();

// gRPC server cho chat-service hỏi thành viên nhóm (Phần 6).
builder.Services.AddGrpc();

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        Description = "Dán accessToken nhận được từ /api/auth/login (không cần gõ chữ Bearer)"
    });
    c.AddSecurityRequirement(document => new OpenApiSecurityRequirement
    {
        [new OpenApiSecuritySchemeReference("Bearer", document)] = []
    });
});

var app = builder.Build();

// Tự áp migration khi khởi động.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<GroupDbContext>();
    await db.Database.MigrateAsync();
}

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/", () => "ChatApp.GroupService is running");
app.MapControllers();

// Chỉ nhận gRPC ở port 5012 (endpoint "Grpc" trong appsettings, HTTP/2). Port REST 5002 không phục vụ gRPC.
// Không [Authorize]: API nội bộ, tin tưởng mạng nội bộ; khi chạy Docker không publish port này ra ngoài.
app.MapGrpcService<GroupMembershipGrpcService>().RequireHost("*:5012");

app.Run();
