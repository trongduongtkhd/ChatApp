using System.Security.Claims;
using ChatApp.Contracts.Grpc;
using Grpc.Net.Client;
using Grpc.Core;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ChatApp.Common.Auth;

public record AccountAccessState(Guid UserId, bool IsLocked, long AuthVersion, string SystemRole);

public interface IAccountAccessReader
{
    Task<IReadOnlyDictionary<Guid, AccountAccessState>> ReadAsync(IReadOnlyCollection<Guid> users, CancellationToken ct);
}

public sealed class RemoteAccountAccessReader(IConfiguration configuration) : IAccountAccessReader, IDisposable
{
    private readonly GrpcChannel channel = GrpcChannel.ForAddress(configuration["GrpcServices:IdentityService"]
        ?? throw new InvalidOperationException("Thiếu GrpcServices:IdentityService"));
    public async Task<IReadOnlyDictionary<Guid, AccountAccessState>> ReadAsync(IReadOnlyCollection<Guid> users, CancellationToken ct)
    {
        var request = new AccountAccessRequest();
        request.UserIds.AddRange(users.Select(u => u.ToString()));
        var reply = await new AccountAccess.AccountAccessClient(channel).GetAccountsAsync(request,
            deadline: DateTime.UtcNow.AddSeconds(3), cancellationToken: ct);
        return reply.Accounts.ToDictionary(a => Guid.Parse(a.UserId), a =>
            new AccountAccessState(Guid.Parse(a.UserId), a.IsLocked, a.AuthVersion, a.SystemRole));
    }
    public void Dispose() => channel.Dispose();
}

public static class AccountAccessRules
{
    public const string SystemAdmin = "SystemAdmin";
    public const string RoleClaim = "system_role";
    public const string VersionClaim = "auth_version";
    public static bool Allows(ClaimsPrincipal user, AccountAccessState? account) =>
        account is { IsLocked: false } &&
        long.TryParse(user.FindFirstValue(VersionClaim) ?? "0", out var version) && version == account.AuthVersion;

    public static IServiceCollection AddRemoteAccountAccess(this IServiceCollection services)
    {
        services.AddSingleton<IAccountAccessReader, RemoteAccountAccessReader>();
        return services;
    }
}

// Sau JWT validation, trước authorization: kiểm nguồn hiện tại cho cả request gọi thẳng service.
public sealed class AccountAccessMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, IAccountAccessReader reader)
    {
        if (context.User.Identity?.IsAuthenticated == true)
        {
            var id = context.User.GetUserId();
            AccountAccessState? account;
            try { account = (await reader.ReadAsync(new[] { id }, context.RequestAborted)).GetValueOrDefault(id); }
            catch (Exception) when (!context.RequestAborted.IsCancellationRequested)
            {
                context.Response.StatusCode = 503;
                await context.Response.WriteAsJsonAsync(new { title = "Chưa kiểm tra được tài khoản. Thử lại sau." });
                return;
            }
            if (!AccountAccessRules.Allows(context.User, account))
            {
                context.Response.StatusCode = 401;
                await context.Response.WriteAsJsonAsync(new { title = "Tài khoản bị khóa hoặc phiên đăng nhập đã hết hiệu lực." });
                return;
            }
            var identity = (ClaimsIdentity)context.User.Identity!;
            foreach (var claim in identity.FindAll(AccountAccessRules.RoleClaim).ToArray()) identity.RemoveClaim(claim);
            identity.AddClaim(new Claim(AccountAccessRules.RoleClaim, account!.SystemRole));
        }
        try { await next(context); }
        catch (RpcException ex) when (!context.Response.HasStarted && ex.StatusCode is StatusCode.Unavailable or StatusCode.DeadlineExceeded)
        {
            context.Response.StatusCode = 503;
            await context.Response.WriteAsJsonAsync(new { title = "Service kiểm tra quyền tạm gián đoạn. Thử lại sau." });
        }
    }
}
