using Confluent.Kafka;
using Microsoft.Extensions.Logging;

namespace ChatApp.Common.Kafka;

// Dùng chung cho producer và consumer: đổi mức log kiểu syslog của librdkafka sang LogLevel của .NET.
internal static class KafkaLogging
{
    public static LogLevel MapLevel(SyslogLevel level) => level switch
    {
        SyslogLevel.Emergency or SyslogLevel.Alert or SyslogLevel.Critical or SyslogLevel.Error => LogLevel.Error,
        SyslogLevel.Warning => LogLevel.Warning,
        SyslogLevel.Notice or SyslogLevel.Info => LogLevel.Information,
        _ => LogLevel.Debug
    };
}
