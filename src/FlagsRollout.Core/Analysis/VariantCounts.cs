namespace FlagsRollout.Core.Analysis;

/// <summary>Requests and failed requests served by one flag variant.</summary>
public readonly record struct VariantCounts(long Requests, long Errors)
{
    public static VariantCounts Zero => default;

    /// <summary>Errors / requests, or 0 when nothing was served (callers check <see cref="Requests"/> first).</summary>
    public double ErrorRate => Requests == 0 ? 0 : (double)Errors / Requests;

    public static VariantCounts operator +(VariantCounts left, VariantCounts right) =>
        new(left.Requests + right.Requests, left.Errors + right.Errors);

    public VariantCounts Add(VariantCounts other) => this + other;
}
