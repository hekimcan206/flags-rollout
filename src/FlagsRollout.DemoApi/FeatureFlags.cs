using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using OpenFeature;
using OpenFeature.Constant;
using OpenFeature.Providers.Flagd;

namespace FlagsRollout.DemoApi;

/// <summary>
/// Owns the flagd provider for this process: registers it under its own OpenFeature domain on start and shuts it
/// down on stop.
/// </summary>
/// <remarks>
/// A named domain instead of the global default provider keeps two hosts in one process (integration tests) from
/// replacing each other's provider through the static <see cref="Api.Instance"/>.
/// </remarks>
public sealed partial class FeatureFlagLifecycle(
    IOptions<FlagdOptions> options,
    ILogger<FeatureFlagLifecycle> logger) : IHostedService
{
    private readonly string _domain = $"demo-api-{Guid.NewGuid():N}";
    private FlagdProvider? _provider;

    public FeatureClient Client => Api.Instance.GetClient(_domain);

    private volatile ProviderStatus _status = ProviderStatus.NotReady;

    public ProviderStatus Status => _status;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var config = FlagdConfig.Builder()
            .WithHost(settings.Host)
            .WithPort(settings.Port)
            .WithResolverType(ResolverType.RPC)
            .WithCache(settings.CacheEnabled)
            .WithLogger(logger)
            .Build();
        _provider = new FlagdProvider(config);

        // The provider reconnects on its own; these events keep readiness in step with it after startup.
        var client = Client;
        client.AddHandler(ProviderEventTypes.ProviderReady, _ => _status = ProviderStatus.Ready);
        client.AddHandler(ProviderEventTypes.ProviderConfigurationChanged, _ => _status = ProviderStatus.Ready);
        client.AddHandler(ProviderEventTypes.ProviderStale, _ => _status = ProviderStatus.Stale);
        client.AddHandler(ProviderEventTypes.ProviderError, _ => _status = ProviderStatus.Error);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(settings.InitTimeoutMs);
        try
        {
            await Api.Instance.SetProviderAsync(_domain, _provider, timeout.Token);
            _status = ProviderStatus.Ready;
            LogReady(settings.Host, settings.Port);
        }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException && !cancellationToken.IsCancellationRequested)
        {
            // Not fatal by design: evaluations return the coded default (the baseline variant) until flagd is back,
            // which is the safe side of every flag in this service. Readiness reports it.
            LogNotReady(settings.Host, settings.Port, settings.InitTimeoutMs);
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_provider is not null)
        {
            await _provider.ShutdownAsync(cancellationToken);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "flagd provider ready at {Host}:{Port}")]
    private partial void LogReady(string host, int port);

    [LoggerMessage(Level = LogLevel.Warning, Message = "flagd at {Host}:{Port} not ready after {TimeoutMs} ms; serving coded defaults")]
    private partial void LogNotReady(string host, int port, int timeoutMs);
}

/// <summary>Ready only when flag evaluations come from flagd rather than from coded defaults.</summary>
public sealed class FlagProviderHealthCheck(FeatureFlagLifecycle flags) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult(flags.Status == ProviderStatus.Ready
            ? HealthCheckResult.Healthy("flagd provider ready")
            : HealthCheckResult.Unhealthy($"flagd provider status {flags.Status}"));
}
