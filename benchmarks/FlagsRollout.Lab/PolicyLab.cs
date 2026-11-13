using System.Globalization;
using System.Text;
using FlagsRollout.Core.Analysis;
using FlagsRollout.Core.Rollout;

namespace FlagsRollout.Lab;

/// <summary>A traffic pattern: request rate and the true error rate of each variant over (simulated) time.</summary>
internal sealed record Scenario(
    string Name,
    string Description,
    double RequestsPerSecond,
    Func<int, double> CanaryErrorRate,
    Func<int, double> BaselineErrorRate,
    int? FaultOnset)
{
    /// <summary>A scenario where the right answer is "do not roll back".</summary>
    public bool CanaryIsBad => FaultOnset is not null;
}

internal sealed record PolicyCase(string Name, IHealthPolicy Policy, int BreachesToRollback);

internal sealed record RunResult(bool RolledBack, int? RollbackAt, bool Promoted, int? PromotedAt, long CanaryErrorsAfterOnset, long CanaryRequestsAfterOnset);

internal sealed record CellResult(
    string Scenario,
    string Policy,
    int Runs,
    double RollbackRate,
    double PromotedRate,
    double? MedianDetectionSeconds,
    double? P95DetectionSeconds,
    double? MedianPromotionSeconds,
    double MedianCanaryErrorsAfterOnset);

/// <summary>
/// Replays simulated canaries through the production <see cref="CounterWindow"/>, policies and
/// <see cref="RolloutEngine"/>. Only the traffic is simulated: one-second steps, every request a Bernoulli trial
/// on the variant's true error rate, deterministic per seed.
/// </summary>
internal static class PolicyLab
{
    public const int DurationSeconds = 900;
    public const int TickSeconds = 5;
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(60);
    public static readonly int[] Steps = [10, 25, 50];
    public static readonly TimeSpan BakeTime = TimeSpan.FromSeconds(120);

    public static IReadOnlyList<Scenario> Scenarios { get; } =
    [
        new("healthy", "both variants fail 1% of 50 req/s", 50, _ => 0.01, _ => 0.01, null),
        new("healthy-low-traffic", "both variants fail 1% of 5 req/s", 5, _ => 0.01, _ => 0.01, null),
        new("shared-incident", "both variants fail 12% for 2 min from t=300 s (downstream outage)", 50,
            t => t is >= 300 and < 420 ? 0.12 : 0.01, t => t is >= 300 and < 420 ? 0.12 : 0.01, null),
        new("subtle-regression", "canary fails 3% vs 1% from the start", 50, _ => 0.03, _ => 0.01, 0),
        new("bad-canary", "canary fails 10% from t=300 s", 50, t => t >= 300 ? 0.10 : 0.01, _ => 0.01, 300),
        new("broken-canary", "canary fails 50% from t=300 s", 50, t => t >= 300 ? 0.50 : 0.01, _ => 0.01, 300),
    ];

    public static IReadOnlyList<PolicyCase> Policies { get; } =
    [
        new("naive threshold (5%, any n, 1 breach)", new ThresholdPolicy(0.05, 1), 1),
        new("guarded threshold (5%, n>=100, 2 breaches)", new ThresholdPolicy(0.05, 100), 2),
        new("relative (z>3.09, +1 pt, n>=100, 2 breaches)", new RelativePolicy(3.09, 0.01, 100), 2),
        new("combined (guarded AND relative)", new CombinedPolicy(new ThresholdPolicy(0.05, 100), new RelativePolicy(3.09, 0.01, 100)), 2),
    ];

    public static List<CellResult> Run(int seeds)
    {
        var results = new List<CellResult>();
        foreach (var scenario in Scenarios)
        {
            foreach (var policy in Policies)
            {
                var runs = Enumerable.Range(0, seeds).Select(seed => Simulate(scenario, policy, seed)).ToList();
                results.Add(Summarise(scenario, policy, runs));
            }
        }

        return results;
    }

    public static RunResult Simulate(Scenario scenario, PolicyCase policy, int seed)
    {
        // Same seed for every policy: each policy judges the same simulated traffic until their decisions diverge.
        var random = new Random((seed * 7919) + scenario.Name.Length);
        var plan = new RolloutPlan(Steps, BakeTime, policy.BreachesToRollback);
        var t0 = DateTimeOffset.UnixEpoch;
        var state = RolloutState.Start(plan, Steps[0], t0);
        var window = new CounterWindow(Window);
        long canaryRequests = 0, canaryErrors = 0, baselineRequests = 0, baselineErrors = 0;
        long errorsAfterOnset = 0, requestsAfterOnset = 0;
        int? rollbackAt = null, promotedAt = null;
        window.Add(Snapshot(t0, 0, 0, 0, 0));

        for (var second = 1; second <= DurationSeconds; second++)
        {
            var share = state.Percent / 100.0;
            var arrivals = Poisson(random, scenario.RequestsPerSecond);
            for (var i = 0; i < arrivals; i++)
            {
                if (random.NextDouble() < share)
                {
                    canaryRequests++;
                    var failed = random.NextDouble() < scenario.CanaryErrorRate(second);
                    canaryErrors += failed ? 1 : 0;
                    if (scenario.FaultOnset is { } onset && second >= onset)
                    {
                        requestsAfterOnset++;
                        errorsAfterOnset += failed ? 1 : 0;
                    }
                }
                else
                {
                    baselineRequests++;
                    baselineErrors += random.NextDouble() < scenario.BaselineErrorRate(second) ? 1 : 0;
                }
            }

            if (second % TickSeconds != 0)
            {
                continue;
            }

            var now = t0.AddSeconds(second);
            window.Add(Snapshot(now, canaryRequests, canaryErrors, baselineRequests, baselineErrors));
            var stats = window.Compute();
            var verdict = policy.Policy.Evaluate(stats.For("on"), stats.For("off"));
            var decision = RolloutEngine.Decide(plan, state, verdict, now);
            state = decision.State;
            switch (decision.Action)
            {
                case RolloutAction.Rollback:
                    rollbackAt = second;
                    break;
                case RolloutAction.SetPercent { Percent: 100 }:
                    promotedAt = second;
                    break;
            }
        }

        return new RunResult(rollbackAt is not null, rollbackAt, promotedAt is not null, promotedAt, errorsAfterOnset, requestsAfterOnset);
    }

    private static CounterSnapshot Snapshot(DateTimeOffset at, long cr, long ce, long br, long be) => new(at, new Dictionary<SeriesKey, VariantCounts>
    {
        [new SeriesKey("sim", "on")] = new(cr, ce),
        [new SeriesKey("sim", "off")] = new(br, be),
    });

    // Knuth's method; fine for the small means used here (<= 50 per second).
    private static int Poisson(Random random, double mean)
    {
        var limit = Math.Exp(-mean);
        var product = random.NextDouble();
        var count = 0;
        while (product > limit)
        {
            count++;
            product *= random.NextDouble();
        }

        return count;
    }

    private static CellResult Summarise(Scenario scenario, PolicyCase policy, List<RunResult> runs)
    {
        var detections = scenario.FaultOnset is { } onset
            ? runs.Where(r => r.RolledBack).Select(r => (double)(r.RollbackAt!.Value - onset)).Order().ToList()
            : [];
        var promotions = runs.Where(r => r.Promoted).Select(r => (double)r.PromotedAt!.Value).Order().ToList();
        return new CellResult(
            scenario.Name,
            policy.Name,
            runs.Count,
            runs.Count(r => r.RolledBack) / (double)runs.Count,
            runs.Count(r => r.Promoted) / (double)runs.Count,
            Percentile(detections, 0.5),
            Percentile(detections, 0.95),
            Percentile(promotions, 0.5),
            Percentile(runs.Select(r => (double)r.CanaryErrorsAfterOnset).Order().ToList(), 0.5) ?? 0);
    }

    /// <summary>Nearest-rank percentile; null for an empty list.</summary>
    public static double? Percentile(IReadOnlyList<double> sorted, double p) =>
        sorted.Count == 0 ? null : sorted[Math.Clamp((int)Math.Ceiling(p * sorted.Count) - 1, 0, sorted.Count - 1)];

    public static string ToMarkdown(IReadOnlyList<CellResult> results)
    {
        var sb = new StringBuilder();
        sb.AppendLine("| scenario | policy | rolled back | promoted | detection p50 / p95 (s after onset) | canary errors served after onset (p50) |");
        sb.AppendLine("|---|---|---:|---:|---:|---:|");
        foreach (var r in results)
        {
            var detection = r.MedianDetectionSeconds is null ? "-" : string.Create(CultureInfo.InvariantCulture, $"{r.MedianDetectionSeconds:0} / {r.P95DetectionSeconds:0}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"| {r.Scenario} | {r.Policy} | {r.RollbackRate * 100:0.#}% | {r.PromotedRate * 100:0.#}% | {detection} | {r.MedianCanaryErrorsAfterOnset:0} |");
        }

        return sb.ToString();
    }
}
