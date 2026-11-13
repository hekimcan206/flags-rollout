namespace FlagsRollout.Core.Analysis;

public enum VerdictKind
{
    /// <summary>Not enough evidence either way: do not promote, do not roll back.</summary>
    Inconclusive,
    Healthy,
    Unhealthy,
}

/// <summary>The outcome of one health check, with a reason a human can read in the audit log.</summary>
public sealed record Verdict(VerdictKind Kind, string Reason)
{
    public static Verdict Healthy(string reason) => new(VerdictKind.Healthy, reason);

    public static Verdict Unhealthy(string reason) => new(VerdictKind.Unhealthy, reason);

    public static Verdict Inconclusive(string reason) => new(VerdictKind.Inconclusive, reason);
}

/// <summary>Judges the canary variant against the baseline variant over the same window.</summary>
public interface IHealthPolicy
{
    string Name { get; }

    Verdict Evaluate(VariantCounts canary, VariantCounts baseline);
}
