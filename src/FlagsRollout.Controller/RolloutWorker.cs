using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace FlagsRollout.Controller;

/// <summary>Runs <see cref="RolloutController.TickAsync"/> on a fixed interval.</summary>
public sealed partial class RolloutWorker(
    RolloutController controller,
    ControllerOptions options,
    TimeProvider time,
    ILogger<RolloutWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var steps = string.Join(",", options.Steps);
        LogStarting(options.FlagKey, options.TickInterval.TotalMilliseconds, options.Window.TotalSeconds, steps);
        using var timer = new PeriodicTimer(options.TickInterval, time);
        do
        {
            try
            {
                await controller.TickAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
#pragma warning disable CA1031 // The loop must survive any single bad tick; the failure is logged and retried.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                LogUnexpected(ex);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Rollout controller for {FlagKey}: tick {TickMs} ms, window {WindowSeconds} s, steps {Steps}")]
    private partial void LogStarting(string flagKey, double tickMs, double windowSeconds, string steps);

    [LoggerMessage(Level = LogLevel.Error, Message = "Unexpected error in rollout tick")]
    private partial void LogUnexpected(Exception ex);
}

/// <summary>Ready while ticks complete: a controller that cannot see its metrics is not protecting anything.</summary>
public sealed class ControllerHealthCheck(RolloutController controller, ControllerOptions options, TimeProvider time) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var status = controller.Status;
        var stale = status.LastTickAt is null || time.GetUtcNow() - status.LastTickAt > options.TickInterval * 3;
        if (stale)
        {
            return Task.FromResult(HealthCheckResult.Unhealthy("no recent tick"));
        }

        return Task.FromResult(status.LastError is null
            ? HealthCheckResult.Healthy(status.Phase)
            : HealthCheckResult.Degraded(status.LastError));
    }
}
