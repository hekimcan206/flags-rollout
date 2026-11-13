using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Configurations;
using FlagsRollout.Controller;
using FlagsRollout.Core.Flags;

namespace FlagsRollout.Lab;

internal sealed record PropagationResult(int Samples, double MedianMs, double P95Ms, double MinMs, double MaxMs);

/// <summary>
/// How long after the controller replaces the flag file does flagd serve the new value? Alternates rollback and
/// restore through <see cref="FlagFileStore"/> against a real flagd and polls its evaluation endpoint every 5 ms.
/// </summary>
internal static class PropagationLab
{
    private static readonly CanaryFlag Flag = new("new-pricing-engine", "on", "off");

    public static async Task<PropagationResult> RunAsync(int samples, string seedFile)
    {
        var directory = Directory.CreateTempSubdirectory("flags-rollout-lab-").FullName;
        var path = Path.Combine(directory, "flags.json");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(directory, (UnixFileMode)0b111_101_101);
        }

        // 100% on as the starting point, so "on" vs "off" is visible for any targeting key.
        var seed = FlagDocument.Parse(await File.ReadAllTextAsync(seedFile)).WithCanaryPercent(Flag, 100, "lab", DateTimeOffset.UtcNow);
        await File.WriteAllTextAsync(path, seed.ToJson());

        await using var flagd = new ContainerBuilder("ghcr.io/open-feature/flagd:v0.18.2")
            .WithBindMount(directory, "/etc/flagd", AccessMode.ReadOnly)
            .WithCommand("start", "--uri", "file:/etc/flagd/flags.json")
            .WithPortBinding(8013, true)
            .WithPortBinding(8014, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPort(8014).ForPath("/readyz")))
            .WithLabel("project", "flags-rollout-lab")
            .Build();
        await flagd.StartAsync();

        using var http = new HttpClient();
        var resolve = new Uri($"http://{flagd.Hostname}:{flagd.GetMappedPublicPort(8013)}/flagd.evaluation.v1.Service/ResolveBoolean");
        async Task<bool> ValueAsync()
        {
            using var response = await http.PostAsJsonAsync(resolve, new { flagKey = Flag.Key, context = new { targetingKey = "lab-customer" } });
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return json.RootElement.TryGetProperty("value", out var v) && v.GetBoolean();
        }

        var store = new FlagFileStore(path);
        var latencies = new List<double>();
        try
        {
            for (var i = 0; i < samples; i++)
            {
                var expectOn = i % 2 == 1;
                var file = await store.ReadAsync(CancellationToken.None);
                var next = expectOn
                    ? file.Document.WithCanaryPercent(Flag, 100, "lab restore", DateTimeOffset.UtcNow)
                    : file.Document.WithRollback(Flag, "lab rollback", DateTimeOffset.UtcNow);

                // Start at a random phase relative to flagd's poll so the sample covers the whole interval.
                await Task.Delay(Random.Shared.Next(0, 1500));
                await store.WriteAsync(next, file.Version, CancellationToken.None);
                var written = Stopwatch.StartNew();
                while (await ValueAsync() != expectOn)
                {
                    if (written.Elapsed > TimeSpan.FromSeconds(30))
                    {
                        throw new TimeoutException("flagd did not pick up the change within 30 s");
                    }

                    await Task.Delay(5);
                }

                latencies.Add(written.Elapsed.TotalMilliseconds);
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }

        var sorted = latencies.Order().ToList();
        return new PropagationResult(
            sorted.Count,
            PolicyLab.Percentile(sorted, 0.5)!.Value,
            PolicyLab.Percentile(sorted, 0.95)!.Value,
            sorted[0],
            sorted[^1]);
    }
}
