using Microsoft.Extensions.Options;
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace FlagsRollout.DemoApi;

public static class ServiceRegistration
{
    public static IServiceCollection AddDemoApi(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<FlagdOptions>().Bind(configuration.GetSection(FlagdOptions.Section)).ValidateDataAnnotations().ValidateOnStart();
        services.AddOptions<CanaryOptions>().Bind(configuration.GetSection(CanaryOptions.Section)).ValidateDataAnnotations().ValidateOnStart();
        services.AddOptions<AdminOptions>().Bind(configuration.GetSection(AdminOptions.Section));
        services.AddOptions<FaultOptions>().Bind(configuration.GetSection(FaultOptions.Section)).ValidateDataAnnotations().ValidateOnStart();

        services.AddSingleton(sp =>
        {
            var faults = sp.GetRequiredService<IOptions<FaultOptions>>().Value;
            return new FaultInjector(faults.CanaryErrorRate, faults.BaselineErrorRate);
        });
        services.AddSingleton<PricingService>();
        services.AddSingleton<RolloutMetrics>();
        services.AddSingleton<FeatureFlagLifecycle>();
        services.AddHostedService(sp => sp.GetRequiredService<FeatureFlagLifecycle>());
        services.AddHealthChecks().AddCheck<FlagProviderHealthCheck>("flagd");
        return services;
    }

    /// <summary>
    /// Metrics go to any OTLP endpoint (Prometheus' OTLP receiver in the demo) when
    /// <c>OTEL_EXPORTER_OTLP_METRICS_ENDPOINT</c> is set; traces likewise with <c>OTEL_EXPORTER_OTLP_TRACES_ENDPOINT</c>.
    /// Without them the service runs with no exporter, which is what unit-style tests want.
    /// </summary>
    public static IServiceCollection AddDemoObservability(this IServiceCollection services, IConfiguration configuration)
    {
        var metricsEndpoint = configuration["OTEL_EXPORTER_OTLP_METRICS_ENDPOINT"];
        var tracesEndpoint = configuration["OTEL_EXPORTER_OTLP_TRACES_ENDPOINT"];
        // The export interval is part of the rollback latency budget (docs/benchmarks.md), so it is explicit.
        var exportIntervalMs = configuration.GetValue("Otel:MetricExportIntervalMs", 5_000);
        var instanceId = configuration["Otel:ServiceInstanceId"] ?? Environment.MachineName + "-" + Environment.ProcessId;

        services.AddOpenTelemetry()
            .ConfigureResource(resource => resource
                .AddService(serviceName: "demo-api", serviceNamespace: "flags-rollout", serviceInstanceId: instanceId))
            .WithMetrics(metrics =>
            {
                metrics.AddMeter(RolloutMetrics.MeterName).AddAspNetCoreInstrumentation();
                if (!string.IsNullOrWhiteSpace(metricsEndpoint))
                {
                    metrics.AddOtlpExporter((exporter, reader) =>
                    {
                        exporter.Endpoint = new Uri(metricsEndpoint);
                        exporter.Protocol = OtlpExportProtocol.HttpProtobuf;
                        reader.PeriodicExportingMetricReaderOptions.ExportIntervalMilliseconds = exportIntervalMs;
                        // Prometheus stores cumulative counters; delta temporality would need its experimental
                        // delta-to-cumulative conversion.
                        reader.TemporalityPreference = MetricReaderTemporalityPreference.Cumulative;
                    });
                }
            })
            .WithTracing(tracing =>
            {
                tracing.AddAspNetCoreInstrumentation(o => o.Filter = ctx => !ctx.Request.Path.StartsWithSegments("/health"));
                if (!string.IsNullOrWhiteSpace(tracesEndpoint))
                {
                    tracing.AddOtlpExporter(o =>
                    {
                        o.Endpoint = new Uri(tracesEndpoint);
                        o.Protocol = OtlpExportProtocol.HttpProtobuf;
                    });
                }
            });
        return services;
    }
}
