using System.Collections.Concurrent;
using Grpc.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;
using Polly.Timeout;

namespace ChatApp.Common.Operations;

// Shared per process/dependency, never per request. Only read-only RPCs use this policy.
public sealed class GrpcResilience(IConfiguration config, ILogger<GrpcResilience> logger)
{
    private readonly ConcurrentDictionary<string, ResiliencePipeline> pipelines = new();
    public async Task<T> ExecuteAsync<T>(string dependency, Func<CancellationToken, Task<T>> action, CancellationToken ct)
    {
        var pipeline = pipelines.GetOrAdd(dependency, Build);
        try
        {
            var value = await pipeline.ExecuteAsync(async token => await action(token), ct);
            OperationsMetrics.DependencyCalls.WithLabels(dependency, "success").Inc(); return value;
        }
        catch (Exception ex) when (ex is BrokenCircuitException or TimeoutRejectedException || ex is OperationCanceledException && !ct.IsCancellationRequested)
        {
            OperationsMetrics.DependencyCalls.WithLabels(dependency, "failure").Inc();
            throw new RpcException(new Status(StatusCode.Unavailable, "Dependency temporarily unavailable."));
        }
        catch { OperationsMetrics.DependencyCalls.WithLabels(dependency, "failure").Inc(); throw; }
    }
    private ResiliencePipeline Build(string dependency)
    {
        OperationsMetrics.Circuit.WithLabels(dependency).Set(0);
        return new ResiliencePipelineBuilder()
            .AddCircuitBreaker(new CircuitBreakerStrategyOptions
            {
                ShouldHandle = new PredicateBuilder().Handle<RpcException>(Transient).Handle<TimeoutRejectedException>(),
                FailureRatio = .5, MinimumThroughput = config.GetValue("Resilience:MinimumThroughput", 6),
                SamplingDuration = TimeSpan.FromSeconds(15), BreakDuration = TimeSpan.FromSeconds(config.GetValue("Resilience:BreakSeconds", 10d)),
                OnOpened = _ => Change("Open", 1), OnHalfOpened = _ => Change("HalfOpen", 2), OnClosed = _ => Change("Closed", 0)
            })
            .AddTimeout(TimeSpan.FromSeconds(config.GetValue("Resilience:TotalSeconds", 3d)))
            .AddRetry(new RetryStrategyOptions
            {
                ShouldHandle = new PredicateBuilder().Handle<RpcException>(Transient), MaxRetryAttempts = 1,
                Delay = TimeSpan.FromMilliseconds(150), BackoffType = DelayBackoffType.Exponential, UseJitter = true,
                OnRetry = _ => { OperationsMetrics.Retries.WithLabels(dependency).Inc(); logger.LogWarning("Retry read-only RPC {Dependency}", dependency); return ValueTask.CompletedTask; }
            }).Build();
        ValueTask Change(string state, double number)
        {
            OperationsMetrics.Circuit.WithLabels(dependency).Set(number);
            logger.LogWarning("Circuit {Dependency} changed to {CircuitState}", dependency, state);
            return ValueTask.CompletedTask;
        }
    }
    private static bool Transient(RpcException ex) => ex.StatusCode is StatusCode.Unavailable or StatusCode.DeadlineExceeded;
}
