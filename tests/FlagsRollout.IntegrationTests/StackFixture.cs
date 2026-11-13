using System.Net.Http.Json;
using System.Text.Json;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Containers;

[assembly: AssemblyFixture(typeof(FlagsRollout.IntegrationTests.StackFixture))]

namespace FlagsRollout.IntegrationTests;

/// <summary>
/// One flagd and one Prometheus for the whole test run, started with the same images and configuration as
/// docker compose. flagd reads a flag file from a host directory, exactly as it reads the shared volume in the demo.
/// </summary>
public sealed class StackFixture : IAsyncLifetime
{
    public const string FlagdImage = "ghcr.io/open-feature/flagd:v0.18.2";
    public const string PrometheusImage = "prom/prometheus:v3.15.1";

    private IContainer _flagd = null!;
    private IContainer _prometheus = null!;

    public string FlagDirectory { get; } = Directory.CreateTempSubdirectory("flags-rollout-it-").FullName;

    public string FlagFile => Path.Combine(FlagDirectory, "flags.json");

    public string FlagdHost => _flagd.Hostname;

    public int FlagdPort => _flagd.GetMappedPublicPort(8013);

    public Uri PrometheusUrl => new($"http://{_prometheus.Hostname}:{_prometheus.GetMappedPublicPort(9090)}/");

    public Uri OtlpMetricsEndpoint => new(PrometheusUrl, "api/v1/otlp/v1/metrics");

    public async ValueTask InitializeAsync()
    {
        if (!OperatingSystem.IsWindows())
        {
            // flagd runs as a non-root user; temp directories are created 0700 on Unix.
            File.SetUnixFileMode(FlagDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }

        await File.WriteAllTextAsync(FlagFile, FlagFiles.Initial("new-pricing-engine", 10));

        _flagd = new ContainerBuilder(FlagdImage)
            .WithBindMount(FlagDirectory, "/etc/flagd", AccessMode.ReadOnly)
            .WithCommand("start", "--uri", "file:/etc/flagd/flags.json")
            .WithPortBinding(8013, true)
            .WithPortBinding(8014, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPort(8014).ForPath("/readyz")))
            .WithLabel("project", "flags-rollout-tests")
            .Build();

        _prometheus = new ContainerBuilder(PrometheusImage)
            .WithResourceMapping(await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "deploy", "prometheus.yml")), "/etc/prometheus/prometheus.yml")
            .WithCommand("--config.file=/etc/prometheus/prometheus.yml", "--storage.tsdb.path=/prometheus", "--web.enable-otlp-receiver")
            .WithPortBinding(9090, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPort(9090).ForPath("/-/ready")))
            .WithLabel("project", "flags-rollout-tests")
            .Build();

        await Task.WhenAll(_flagd.StartAsync(), _prometheus.StartAsync());
    }

    public async ValueTask DisposeAsync()
    {
        await _flagd.DisposeAsync();
        await _prometheus.DisposeAsync();
        Directory.Delete(FlagDirectory, recursive: true);
    }

    /// <summary>Evaluates a boolean flag through flagd's Connect/JSON endpoint (the same resolver the SDK calls).</summary>
    public async Task<(bool Value, string Variant, string Reason)> ResolveAsync(HttpClient http, string flagKey, string targetingKey, CancellationToken ct)
    {
        using var response = await http.PostAsJsonAsync(
            new Uri($"http://{FlagdHost}:{FlagdPort}/flagd.evaluation.v1.Service/ResolveBoolean"),
            new { flagKey, context = new { targetingKey } },
            ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            // The previous test's file defines other flags; flagd may not have loaded this test's file yet.
            return (false, string.Empty, "FLAG_NOT_FOUND");
        }

        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var root = json.RootElement;
        return (root.TryGetProperty("value", out var v) && v.GetBoolean(), root.GetProperty("variant").GetString()!, root.GetProperty("reason").GetString()!);
    }

    public async Task<string> ResolveStringAsync(HttpClient http, string flagKey, string targetingKey, string email, CancellationToken ct)
    {
        using var response = await http.PostAsJsonAsync(
            new Uri($"http://{FlagdHost}:{FlagdPort}/flagd.evaluation.v1.Service/ResolveString"),
            new { flagKey, context = new { targetingKey, email } },
            ct);
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return json.RootElement.GetProperty("value").GetString()!;
    }

    /// <summary>Polls until <paramref name="condition"/> holds; returns how long it took.</summary>
    public static async Task<TimeSpan> EventuallyAsync(Func<Task<bool>> condition, TimeSpan timeout, string what)
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
        while (started.Elapsed < timeout)
        {
            if (await condition())
            {
                return started.Elapsed;
            }

            await Task.Delay(50);
        }

        throw new TimeoutException($"Timed out after {timeout.TotalSeconds:0}s waiting for {what}.");
    }
}

/// <summary>Flag files used by the tests.</summary>
public static class FlagFiles
{
    public static string Initial(string flagKey, int percent) => $$"""
        {
          "$schema": "https://flagd.dev/schema/v0/flags.json",
          "flags": {
            "{{flagKey}}": {
              "state": "ENABLED",
              "variants": { "on": true, "off": false },
              "defaultVariant": "off",
              "targeting": {
                "fractional": [
                  { "cat": [ { "var": "$flagd.flagKey" }, { "var": "targetingKey" } ] },
                  [ "on", {{percent}} ],
                  [ "off", {{100 - percent}} ]
                ]
              }
            }
          }
        }
        """;
}
