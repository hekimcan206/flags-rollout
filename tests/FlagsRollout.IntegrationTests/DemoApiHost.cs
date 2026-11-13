using System.Net.Http.Json;
using FlagsRollout.DemoApi;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace FlagsRollout.IntegrationTests;

/// <summary>The demo API hosted in-process, wired to the fixture's flagd and pushing OTLP metrics to its Prometheus.</summary>
public sealed class DemoApiHost : WebApplicationFactory<Program>
{
    public const string AdminKey = "it-admin-key";

    private readonly StackFixture _stack;

    public DemoApiHost(StackFixture stack, string flagKey)
    {
        _stack = stack;
        FlagKey = flagKey;
        InstanceId = $"it-{Guid.NewGuid():N}"[..15];
    }

    public string FlagKey { get; }

    public string InstanceId { get; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Flagd:Host", _stack.FlagdHost);
        builder.UseSetting("Flagd:Port", _stack.FlagdPort.ToString(System.Globalization.CultureInfo.InvariantCulture));
        builder.UseSetting("Canary:FlagKey", FlagKey);
        builder.UseSetting("Admin:ApiKey", AdminKey);
        builder.UseSetting("OTEL_EXPORTER_OTLP_METRICS_ENDPOINT", _stack.OtlpMetricsEndpoint.ToString());
        builder.UseSetting("Otel:MetricExportIntervalMs", "500");
        builder.UseSetting("Otel:ServiceInstanceId", InstanceId);
        builder.UseSetting("Serilog:MinimumLevel:Default", "Warning");
    }

    public async Task SetFaultsAsync(HttpClient client, double canary, double baseline)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, "/admin/faults")
        {
            Content = JsonContent.Create(new FaultSettings(canary, baseline)),
        };
        request.Headers.Add("X-Admin-Key", AdminKey);
        using var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
    }
}
