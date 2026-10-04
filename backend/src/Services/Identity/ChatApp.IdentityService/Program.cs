using ChatApp.Common.Auth;
using ChatApp.Common.Outbox;
using ChatApp.IdentityService.Data;
using ChatApp.IdentityService.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi;

var builder = WebApplication.CreateBuilder(args);

// Connection string lấy từ cấu hình: user-secrets khi chạy local, biến môi trường khi chạy Docker.
builder.Services.AddDbContext<IdentityDbContext>(options =>
    options
        .UseNpgsql(builder.Configuration.GetConnectionString("IdentityDb"))
        .UseSnakeCaseNamingConvention());

// Kiểm tra JWT (dùng chung với các service khác) + đăng ký JwtOptions để JwtTokenService tạo token.
builder.Services.AddChatAppJwtAuthentication(builder.Configuration);

// Kafka producer + OutboxPublisher (BackgroundService) đẩy outbox_messages của identity_db lên Kafka.
builder.Services.AddChatAppOutbox<IdentityDbContext>(builder.Configuration);

builder.Services.AddSingleton<JwtTokenService>();
builder.Services.AddScoped<AuthService>();

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    // Nút "Authorize" trong Swagger UI: dán token vào để gọi các API cần đăng nhập.
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

// Tự áp migration khi khởi động: database luôn đúng schema mà không phải chạy lệnh tay.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
    await db.Database.MigrateAsync();
}

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// Thứ tự quan trọng: xác thực (bạn là ai) trước, phân quyền (được làm gì) sau.
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/", () => "ChatApp.IdentityService is running");
app.MapControllers();

app.Run();
