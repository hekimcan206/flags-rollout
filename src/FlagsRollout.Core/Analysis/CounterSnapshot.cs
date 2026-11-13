namespace FlagsRollout.Core.Analysis;

/// <summary>
/// One time series of the request counter: a single service instance serving a single variant. Instances are kept
/// apart because a counter reset (process restart) is only detectable per series; summing first would hide it.
/// </summary>
public readonly record struct SeriesKey(string Instance, string Variant);

/// <summary>Cumulative counter values of every series, read at one instant.</summary>
public sealed record CounterSnapshot(DateTimeOffset At, IReadOnlyDictionary<SeriesKey, VariantCounts> Series)
{
    public static CounterSnapshot Empty(DateTimeOffset at) => new(at, new Dictionary<SeriesKey, VariantCounts>());
}
