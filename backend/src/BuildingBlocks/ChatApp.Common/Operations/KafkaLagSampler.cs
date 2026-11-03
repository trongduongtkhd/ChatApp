using ChatApp.Common.Backup;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Prometheus;

namespace ChatApp.Common.Operations;

// Metadata/committed-offset queries only: never joins groups or changes offsets.
public sealed class KafkaLagSampler(IConfiguration config, ILogger<KafkaLagSampler> logger) : BackgroundService
{
    private readonly Gauge lag = Metrics.CreateGauge("chatapp_kafka_consumer_lag", "High watermark minus committed group offset.", new GaugeConfiguration { LabelNames = ["group", "topic", "partition"] });
    private readonly Gauge available = Metrics.CreateGauge("chatapp_kafka_monitor_up", "Whether the last Kafka metadata sample succeeded.");
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var point = await Task.Run(() => KafkaBackup.Capture(config["Kafka:BootstrapServers"] ?? "localhost:9092"), ct);
                foreach (var partition in point.Partitions)
                    foreach (var group in partition.Groups)
                        lag.WithLabels(group.Key, partition.Topic, partition.Partition.ToString()).Set(Math.Max(0, partition.End - group.Value));
                available.Set(1);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested) { available.Set(0); logger.LogDebug("Kafka monitoring unavailable: {Error}", ex.Message); }
            try { await Task.Delay(15000, ct); } catch (OperationCanceledException) { break; }
        }
    }
}
