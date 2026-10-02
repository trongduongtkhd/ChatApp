using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;

namespace ChatApp.Common.Auth;

public static class JwtAuthenticationExtensions
{
    // Cấu hình KIỂM TRA JWT. Mọi service (và Gateway) gọi hàm này,
    // tự xác minh token bằng secret chung mà không cần hỏi identity-service (stateless).
    public static IServiceCollection AddChatAppJwtAuthentication(
        this IServiceCollection services, IConfiguration configuration)
    {
        var section = configuration.GetSection(JwtOptions.SectionName);
        var jwt = section.Get<JwtOptions>() ?? new JwtOptions();
        jwt.Validate();

        services.Configure<JwtOptions>(section);

        services
            .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                // Giữ nguyên tên claim "sub", "name"... thay vì đổi sang URL dài kiểu
                // http://schemas.xmlsoap.org/ws/2005/05/identity/claims/nameidentifier.
                options.MapInboundClaims = false;

                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = jwt.Issuer,
                    ValidateAudience = true,
                    ValidAudience = jwt.Audience,
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = jwt.GetSigningKey(),
                    // Cho phép lệch đồng hồ giữa các máy tối đa 30 giây (mặc định là 5 phút).
                    ClockSkew = TimeSpan.FromSeconds(30),
                    NameClaimType = ChatAppClaims.UserName
                };
            });

        services.AddAuthorization();
        return services;
    }
}
