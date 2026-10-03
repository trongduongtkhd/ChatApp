using ChatApp.Common.Auth;
using Microsoft.AspNetCore.Authorization;

var builder = WebApplication.CreateBuilder(args);

// Cùng code kiểm tra JWT với các service (ChatApp.Common), cùng Jwt:Secret trong kho user-secrets chung.
builder.Services.AddChatAppJwtAuthentication(builder.Configuration);

builder.Services.AddAuthorization(options =>
{
    // Lưới an toàn: route nào quên khai báo AuthorizationPolicy cũng mặc định phải đăng nhập.
    options.FallbackPolicy = new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build();
});

// YARP đọc bảng Routes/Clusters từ mục "ReverseProxy" trong cấu hình.
// Mỗi Route có AuthorizationPolicy: "anonymous" (ai cũng vào) hoặc "default" (phải có JWT hợp lệ).
builder.Services
    .AddReverseProxy()
    .LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"));

var app = builder.Build();

// Xác thực (đọc + kiểm tra token) trước, phân quyền (route này có cho vào không) sau.
// Request bị chặn ở đây thì KHÔNG được chuyển tiếp tới service.
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/", () => "ChatApp.Gateway is running").AllowAnonymous();

// Mọi request khớp một Route sẽ được chuyển tiếp nguyên vẹn (method, header, body) tới service.
app.MapReverseProxy();

app.Run();
