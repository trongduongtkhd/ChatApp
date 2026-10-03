using ChatApp.Common.Auth;
using ChatApp.GroupService.Data;
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

builder.Services.AddScoped<GroupManagementService>();

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

app.Run();
