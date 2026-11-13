using System.Globalization;

namespace FlagsRollout.Core.Analysis;

/// <summary>
/// Unhealthy when the canary fails measurably more often than the baseline serving the same traffic at the same time.
/// </summary>
/// <remarks>
/// One-sided two-proportion z-test with a pooled standard error, plus an effect-size floor: with enough traffic a
/// 0.1 percentage-point difference becomes "significant" while nobody would roll back for it. Comparing against the
/// baseline (instead of a fixed number) is what keeps a shared incident, such as a slow database that hurts both
/// variants, from being blamed on the canary.
/// </remarks>
public sealed class RelativePolicy(double zCritical, double minAbsoluteIncrease, long minRequests) : IHealthPolicy
{
    public string Name => string.Create(
        CultureInfo.InvariantCulture,
        $"relative z>{zCritical:0.##} +{minAbsoluteIncrease * 100:0.##}% n>={minRequests}");

    public Verdict Evaluate(VariantCounts canary, VariantCounts baseline)
    {
        if (canary.Requests < minRequests || baseline.Requests < minRequests)
        {
            return Verdict.Inconclusive(Format(
                $"need {minRequests} requests per variant, have canary {canary.Requests}, baseline {baseline.Requests}"));
        }

        var z = ZScore(canary, baseline);
        var difference = canary.ErrorRate - baseline.ErrorRate;
        var summary = Format(
            $"canary {canary.ErrorRate * 100:0.00}% ({canary.Errors}/{canary.Requests}) vs baseline {baseline.ErrorRate * 100:0.00}% ({baseline.Errors}/{baseline.Requests}), z={z:0.00}");

        return z > zCritical && difference > minAbsoluteIncrease
            ? Verdict.Unhealthy(summary)
            : Verdict.Healthy(summary);
    }

    /// <summary>z statistic of "canary error rate &gt; baseline error rate"; 0 when both rates are 0 or both are 1.</summary>
    public static double ZScore(VariantCounts canary, VariantCounts baseline)
    {
        if (canary.Requests == 0 || baseline.Requests == 0)
        {
            return 0;
        }

        var pooled = (double)(canary.Errors + baseline.Errors) / (canary.Requests + baseline.Requests);
        var standardError = Math.Sqrt(pooled * (1 - pooled) * ((1.0 / canary.Requests) + (1.0 / baseline.Requests)));
        return standardError == 0 ? 0 : (canary.ErrorRate - baseline.ErrorRate) / standardError;
    }

    private static string Format(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
