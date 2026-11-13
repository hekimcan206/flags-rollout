using FlagsRollout.Core.Analysis;
using FlagsRollout.Core.Flags;
using FlagsRollout.Core.Rollout;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;

namespace FlagsRollout.UnitTests;

/// <summary>Invariants that must hold for every input, checked on generated cases.</summary>
public sealed class PropertyTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 13, 9, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// The window's count equals the true number of requests in it, whatever pattern of process restarts happened,
    /// as long as no restart hides between two reads (one read per tick is the contract).
    /// </summary>
    [Property(MaxTest = 300)]
    public bool Window_counts_equal_true_increments_across_restarts(NonEmptyArray<PositiveInt> traffic, int seed)
    {
        var random = new Random(seed);
        var window = new CounterWindow(TimeSpan.FromDays(1));
        long cumulativeRequests = 0, cumulativeErrors = 0, trueRequests = 0, trueErrors = 0;
        window.Add(new CounterSnapshot(T0, new Dictionary<SeriesKey, VariantCounts>()));

        var tick = 0;
        var hiddenRestart = false;
        foreach (var served in traffic.Get)
        {
            var requests = served.Get % 1000;
            var errors = random.Next(0, requests + 1);
            if (random.Next(5) == 0)
            {
                // Restart: the counter starts again from zero and this tick's traffic is all it has.
                // It is only visible if the new value is lower than the last read.
                hiddenRestart |= requests >= cumulativeRequests || errors >= cumulativeErrors;
                cumulativeRequests = 0;
                cumulativeErrors = 0;
            }

            cumulativeRequests += requests;
            cumulativeErrors += errors;
            trueRequests += requests;
            trueErrors += errors;
            tick++;
            window.Add(new CounterSnapshot(
                T0.AddSeconds(tick),
                new Dictionary<SeriesKey, VariantCounts> { [new("a", "on")] = new(cumulativeRequests, cumulativeErrors) }));
        }

        var counted = window.Compute().For("on");
        // A restart after which the counter is already back above its last read is invisible to any reader of
        // cumulative values (Prometheus included) and under-counts; nothing may ever over-count.
        return counted.Requests <= trueRequests && counted.Errors <= trueErrors && counted.Errors <= counted.Requests &&
               (hiddenRestart || (counted.Requests == trueRequests && counted.Errors == trueErrors));
    }

    [Property(MaxTest = 300)]
    public bool Window_counts_are_never_negative(PositiveInt[] values, int seed)
    {
        var random = new Random(seed);
        var window = new CounterWindow(TimeSpan.FromSeconds(30));
        for (var i = 0; i < values.Length; i++)
        {
            var requests = values[i].Get;
            window.Add(new CounterSnapshot(
                T0.AddSeconds(i * 5),
                new Dictionary<SeriesKey, VariantCounts> { [new("a", "on")] = new(requests, random.Next(0, requests + 1)) }));
        }

        var counts = window.Compute().For("on");
        return counts.Requests >= 0 && counts.Errors >= 0 && counts.Errors <= counts.Requests;
    }

    [Property(MaxTest = 500)]
    public bool Relative_policy_never_blames_a_canary_that_fails_less_often(PositiveInt n1, PositiveInt n2, int e1, int e2)
    {
        var canaryRequests = (n1.Get % 50_000) + 100;
        var baselineRequests = (n2.Get % 50_000) + 100;
        var canary = new VariantCounts(canaryRequests, Math.Abs(e1 % (canaryRequests + 1)));
        var baseline = new VariantCounts(baselineRequests, Math.Abs(e2 % (baselineRequests + 1)));
        if (canary.ErrorRate > baseline.ErrorRate)
        {
            return true;
        }

        return new RelativePolicy(1.0, 0, 100).Evaluate(canary, baseline).Kind != VerdictKind.Unhealthy;
    }

    [Property(MaxTest = 300)]
    public bool Rewrites_round_trip_the_percentage_and_raising_it_keeps_the_bucket_order(PositiveInt a, PositiveInt b)
    {
        var flag = new CanaryFlag("f", "on", "off");
        var low = (a.Get % 99) + 1;
        var high = Math.Max(low, (b.Get % 99) + 1);
        var doc = FlagDocument.Parse("""{ "flags": { "f": { "state": "ENABLED", "variants": { "on": true, "off": false }, "defaultVariant": "off" } } }""");

        var atLow = doc.WithCanaryPercent(flag, low, "r", T0);
        var atHigh = atLow.WithCanaryPercent(flag, high, "r", T0);

        return atLow.GetCanaryPercent(flag) == low &&
               atHigh.GetCanaryPercent(flag) == high &&
               FirstBucket(atHigh) == "on";
    }

    internal static string FirstBucket(FlagDocument document)
    {
        using var json = System.Text.Json.JsonDocument.Parse(document.ToJson());
        return json.RootElement.GetProperty("flags").GetProperty("f").GetProperty("targeting").GetProperty("fractional")[1][0].GetString()!;
    }

    /// <summary>
    /// Whatever sequence of verdicts arrives, a rolled-back rollout stays rolled back, the percentage only moves up the
    /// plan or to 0, and promotion only happens after a healthy verdict.
    /// </summary>
    [Property(MaxTest = 500)]
    public Property Engine_is_monotonic_and_rollback_is_terminal()
    {
        var verdicts = Gen.Elements(VerdictKind.Healthy, VerdictKind.Unhealthy, VerdictKind.Inconclusive).ArrayOf();
        return Prop.ForAll(verdicts.ToArbitrary(), sequence =>
        {
            var plan = new RolloutPlan([5, 20, 60], TimeSpan.FromSeconds(10), 2);
            var state = RolloutState.Start(plan, 5, T0);
            var rolledBack = false;
            for (var i = 0; i < sequence.Length; i++)
            {
                var verdict = new Verdict(sequence[i], "generated");
                var decision = RolloutEngine.Decide(plan, state, verdict, T0.AddSeconds((i + 1) * 5));
                if (rolledBack && decision.Action is not RolloutAction.None)
                {
                    return false;
                }

                if (decision.State.Percent != 0 && decision.State.Percent < state.Percent)
                {
                    return false;
                }

                if (decision.Action is RolloutAction.SetPercent && sequence[i] != VerdictKind.Healthy)
                {
                    return false;
                }

                if (decision.Action is RolloutAction.Rollback && sequence[i] != VerdictKind.Unhealthy)
                {
                    return false;
                }

                rolledBack |= decision.State.Phase == RolloutPhase.RolledBack;
                state = decision.State;
            }

            return true;
        });
    }
}
