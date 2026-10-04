using ChatApp.Common.Kafka;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ChatApp.Common.Outbox;

public static class OutboxServiceCollectionExtensions
{
    // Service phát sự kiện gọi MỘT hàm này: đăng ký Kafka producer + tiến trình nền đẩy outbox.
    // DbContext TDbContext phải gọi modelBuilder.AddOutboxMessages() và đã có migration tạo bảng.
    public static IServiceCollection AddChatAppOutbox<TDbContext>(
        this IServiceCollection services, IConfiguration configuration)
        where TDbContext : DbContext
    {
        services.AddChatAppKafkaProducer(configuration);
        services.AddHostedService<OutboxPublisher<TDbContext>>();
        return services;
    }
}
