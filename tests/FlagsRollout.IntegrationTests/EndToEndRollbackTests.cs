using System.Diagnostics;
using System.Net.Http.Json;
using FlagsRollout.Controller;
using FlagsRollout.Core.Flags;
using FlagsRollout.DemoApi;
using Microsoft.Extensions.Logging.Abstractions;

namespace FlagsRollout.IntegrationTests;

/// <summary>
/// The whole loop with nothing faked: the API serves traffic through flagd, pushes counts to Prometheus, the controller
/// reads Prometheus and rewrites the flag file, flagd picks it up and the API changes behaviour.
/// </summary>
public sealed class EndToEndRollbackTests(StackFixture stack)
{
    private const string FlagKey = "e2e-pricing";

    [Fact]
    public async Task A_healthy_canary_advances_then_a_broken_one_is_rolled_back_and_traffic_returns_to_baseline()
    {
        var ct = TestContext.Current.CancellationToken;
        await File.WriteAllTextAsync(stack.FlagFile, FlagFiles.Initial(FlagKey, 10), ct);
        await using var host = new DemoApiHost(stack, FlagKey);
        using var client = host.CreateClient();
        await StackFixture.EventuallyAsync(
            async () => (await client.GetFromJsonAsync<QuoteResponse>("/api/v1/quote?customerId=probe", ct))!.Variant is "on" or "off",
            TimeSpan.FromSeconds(20),
            "the API to read the e2e flag");
        await host.SetFaultsAsync(client, canary: 0, baseline: 0.005);

        var options = new ControllerOptions
        {
            FlagFile = stack.FlagFile,
            FlagKey = FlagKey,
            PrometheusUrl = stack.PrometheusUrl.ToString(),
            TickInterval = TimeSpan.FromSeconds(1),
            Window = TimeSpan.FromSeconds(10),
            Steps = [10, 50],
            BakeTime = TimeSpan.FromSeconds(4),
            BreachesToRollback = 2,
            Policy = PolicyKind.Combined,
            MinRequests = 30,
        };
        using var prometheus = new HttpClient { BaseAddress = stack.PrometheusUrl };
        using var meters = new TestMeterFactory();
        var controller = new RolloutController(
            options, new FlagFileStore(options.FlagFile), new PrometheusCounterSource(prometheus, options), TimeProvider.System, meters, NullLogger<RolloutController>.Instance);

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var load = new LoadLoop(client);
        var traffic = load.RunAsync(stop.Token);
        var loop = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                await controller.TickAsync(stop.Token);
                await Task.Delay(options.TickInterval, stop.Token);
            }
        }, stop.Token);

        try
        {
            await StackFixture.EventuallyAsync(() => Task.FromResult(controller.Status.Percent == 50), TimeSpan.FromSeconds(60), "the canary to advance to 50%");

            await host.SetFaultsAsync(client, canary: 0.5, baseline: 0.005);
            var faultAt = Stopwatch.GetTimestamp();
            load.MarkFault();

            var decided = await StackFixture.EventuallyAsync(() => Task.FromResult(controller.Status.Phase == "rolled-back"), TimeSpan.FromSeconds(60), "the rollback");
            var served = await StackFixture.EventuallyAsync(() => Task.FromResult(load.LastVariantSeenOnAfter(faultAt) == "off" && load.OnSince(TimeSpan.FromSeconds(1)) == 0), TimeSpan.FromSeconds(30), "traffic to stop reaching the canary");

            var file = FlagDocument.Parse(await File.ReadAllTextAsync(stack.FlagFile, ct));
            var flag = new CanaryFlag(FlagKey, "on", "off");
            file.GetCanaryPercent(flag).Should().Be(0);
            file.GetRolloutMetadata(flag)!.Reason.Should().Contain("unhealthy checks in a row at 50%");
            TestContext.Current.SendDiagnosticMessage(
                $"fault -> rollback decision {decided.TotalSeconds:0.0} s; -> canary traffic stopped {decided.TotalSeconds + served.TotalSeconds:0.0} s; canary errors served after fault: {load.CanaryErrorsAfterFault}");
        }
        finally
        {
            await stop.CancelAsync();
            await Task.WhenAll(traffic, loop).ContinueWith(_ => { }, TaskScheduler.Default);
        }
    }

    /// <summary>Steady synthetic traffic: one request every few milliseconds from a rotating set of customers.</summary>
    private sealed class LoadLoop(HttpClient client)
    {
        private readonly Lock _gate = new();
        private readonly List<(long At, string Variant)> _seen = [];
        private long _faultAt = long.MaxValue;
        private int _canaryErrorsAfterFault;

        public int CanaryErrorsAfterFault => Volatile.Read(ref _canaryErrorsAfterFault);

        public void MarkFault() => Interlocked.Exchange(ref _faultAt, Stopwatch.GetTimestamp());

        public async Task RunAsync(CancellationToken ct)
        {
            var i = 0;
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    using var response = await client.GetAsync(new Uri($"/api/v1/quote?customerId=load-{i++ % 5000}", UriKind.Relative), ct);
                    var variant = response.IsSuccessStatusCode
                        ? (await response.Content.ReadFromJsonAsync<QuoteResponse>(ct))!.Variant
                        : "on";
                    var now = Stopwatch.GetTimestamp();
                    if (!response.IsSuccessStatusCode && now > Interlocked.Read(ref _faultAt))
                    {
                        Interlocked.Increment(ref _canaryErrorsAfterFault);
                    }

                    lock (_gate)
                    {
                        _seen.Add((now, variant));
                    }

                    await Task.Delay(5, ct);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }

        public string? LastVariantSeenOnAfter(long timestamp)
        {
            lock (_gate)
            {
                return _seen.Count > 0 && _seen[^1].At > timestamp ? _seen[^1].Variant : null;
            }
        }

        public int OnSince(TimeSpan span)
        {
            var since = Stopwatch.GetTimestamp() - (long)(span.TotalSeconds * Stopwatch.Frequency);
            lock (_gate)
            {
                return _seen.Count(s => s.At >= since && s.Variant == "on");
            }
        }
    }
}
