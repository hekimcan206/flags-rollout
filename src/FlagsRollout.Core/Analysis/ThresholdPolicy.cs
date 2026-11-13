using System.Globalization;

namespace FlagsRollout.Core.Analysis;

/// <summary>
/// Unhealthy when the canary's error rate is above a fixed ceiling. This is what "auto-disable the flag when the
/// error rate crosses a threshold" means literally; <paramref name="minRequests"/> keeps it from judging a handful of
/// requests (one failure out of three is a 33% error rate).
/// </summary>
public sealed class ThresholdPolicy(double maxErrorRate, long minRequests) : IHealthPolicy
{
    public string Name => minRequests <= 1
        ? string.Create(CultureInfo.InvariantCulture, $"threshold>{maxErrorRate * 100:0.##}% (no minimum)")
        : string.Create(CultureInfo.InvariantCulture, $"threshold>{maxErrorRate * 100:0.##}% n>={minRequests}");

    public double MaxErrorRate => maxErrorRate;

    public Verdict Evaluate(VariantCounts canary, VariantCounts baseline)
    {
        if (canary.Requests < Math.Max(1, minRequests))
        {
            return Verdict.Inconclusive(Format($"canary served {canary.Requests} requests, need {Math.Max(1, minRequests)}"));
        }

        return canary.ErrorRate > maxErrorRate
            ? Verdict.Unhealthy(Format($"canary error rate {canary.ErrorRate * 100:0.00}% ({canary.Errors}/{canary.Requests}) above {maxErrorRate * 100:0.00}%"))
            : Verdict.Healthy(Format($"canary error rate {canary.ErrorRate * 100:0.00}% ({canary.Errors}/{canary.Requests}) within {maxErrorRate * 100:0.00}%"));
    }

    private static string Format(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
