using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using Serilog;
using Serilog.Formatting.Compact;

namespace FlagsRollout.Controller;

/// <summary>
/// Entry point. A named class rather than top-level statements: the demo API's <c>Program</c> is the one integration
/// tests host, and two global <c>Program</c> types in one test assembly would collide.
/// </summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        // `seed <source> <target>`: copy the initial flag file into the shared volume if it is not there yet. Run once
        // by docker compose before flagd and the controller start, so a restart never resets a rollout in progress.
        if (args is ["seed", var source, var target])
        {
            return Seed(source, target);
        }

        var builder = WebApplication.CreateBuilder(args);

        builder.Host.UseSerilog((context, logger) => logger
            .ReadFrom.Configuration(context.Configuration)
            .Enrich.FromLogContext()
            .WriteTo.Console(new RenderedCompactJsonFormatter()));

        builder.Services.AddRolloutController(builder.Configuration);

        var metricsEndpoint = builder.Configuration["OTEL_EXPORTER_OTLP_METRICS_ENDPOINT"];
        builder.Services.AddOpenTelemetry()
            .ConfigureResource(r => r.AddService("rollout-controller", serviceNamespace: "flags-rollout"))
            .WithMetrics(metrics =>
            {
                metrics.AddMeter(ControllerTelemetry.MeterName).AddHttpClientInstrumentation();
                if (!string.IsNullOrWhiteSpace(metricsEndpoint))
                {
                    metrics.AddOtlpExporter((exporter, reader) =>
                    {
                        exporter.Endpoint = new Uri(metricsEndpoint);
                        exporter.Protocol = OtlpExportProtocol.HttpProtobuf;
                        reader.TemporalityPreference = MetricReaderTemporalityPreference.Cumulative;
                    });
                }
            });

        var app = builder.Build();

        app.MapGet("/status", (RolloutController controller) => controller.Status);
        app.MapHealthChecks("/health/live", new() { Predicate = _ => false });
        app.MapHealthChecks("/health/ready");

        await app.RunAsync();
        return 0;
    }

    private static int Seed(string source, string target)
    {
        if (File.Exists(target))
        {
            Console.WriteLine($"flag file {target} exists; seed skipped");
            return 0;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(target))!);
        File.Copy(source, target);
        Console.WriteLine($"seeded {target} from {source}");
        return 0;
    }
}
