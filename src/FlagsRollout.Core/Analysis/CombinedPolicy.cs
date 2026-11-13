namespace FlagsRollout.Core.Analysis;

/// <summary>
/// Unhealthy only when the canary is both above the absolute ceiling and significantly worse than the baseline.
/// </summary>
/// <remarks>
/// The ceiling answers "is this bad enough to act on?" and the relative test answers "is it the canary's fault?".
/// Either one alone fails a scenario in the lab (docs/benchmarks.md): the threshold alone rolls back during shared
/// incidents, the relative test alone rolls back on harmless, statistically real differences.
/// </remarks>
public sealed class CombinedPolicy(ThresholdPolicy threshold, RelativePolicy relative) : IHealthPolicy
{
    public string Name => $"{threshold.Name} AND {relative.Name}";

    public Verdict Evaluate(VariantCounts canary, VariantCounts baseline)
    {
        var absolute = threshold.Evaluate(canary, baseline);
        var compared = relative.Evaluate(canary, baseline);

        if (absolute.Kind == VerdictKind.Inconclusive || compared.Kind == VerdictKind.Inconclusive)
        {
            return Verdict.Inconclusive(absolute.Kind == VerdictKind.Inconclusive ? absolute.Reason : compared.Reason);
        }

        return absolute.Kind == VerdictKind.Unhealthy && compared.Kind == VerdictKind.Unhealthy
            ? Verdict.Unhealthy(compared.Reason)
            : Verdict.Healthy(compared.Reason);
    }
}
