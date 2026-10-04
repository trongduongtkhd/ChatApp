using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ChatApp.Common.Kafka;

public static class KafkaServiceCollectionExtensions
{
    // Đăng ký producer dùng chung. Service nào phát sự kiện (qua outbox) thì gọi hàm này.
    public static IServiceCollection AddChatAppKafkaProducer(
        this IServiceCollection services, IConfiguration configuration)
    {
        var section = configuration.GetSection(KafkaOptions.SectionName);
        var kafka = section.Get<KafkaOptions>() ?? new KafkaOptions();
        kafka.Validate();

        services.Configure<KafkaOptions>(section);
        services.AddSingleton<IKafkaProducer, KafkaProducer>();
        return services;
    }

    // Đăng ký một consumer (lớp kế thừa KafkaConsumerBase) chạy nền suốt đời service.
    public static IServiceCollection AddChatAppKafkaConsumer<TConsumer>(
        this IServiceCollection services, IConfiguration configuration)
        where TConsumer : BackgroundService
    {
        var section = configuration.GetSection(KafkaOptions.SectionName);
        var kafka = section.Get<KafkaOptions>() ?? new KafkaOptions();
        kafka.ValidateConsumer();

        services.Configure<KafkaOptions>(section);
        services.AddHostedService<TConsumer>();
        return services;
    }
}
