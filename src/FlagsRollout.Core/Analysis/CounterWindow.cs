namespace FlagsRollout.Core.Analysis;

/// <summary>Requests and errors per variant that happened inside the window, and how much time the data covers.</summary>
public sealed record WindowStats(IReadOnlyDictionary<string, VariantCounts> Variants, TimeSpan Covered)
{
    public static WindowStats Empty { get; } = new(new Dictionary<string, VariantCounts>(), TimeSpan.Zero);

    public VariantCounts For(string variant) => Variants.TryGetValue(variant, out var counts) ? counts : VariantCounts.Zero;
}

/// <summary>
/// Turns a stream of cumulative counter snapshots into "what happened in the last <c>length</c>".
/// </summary>
/// <remarks>
/// The arithmetic is Prometheus' <c>increase()</c> without extrapolation: increments are summed between consecutive
/// snapshots, and a value that went down means the series restarted from zero, so the whole new value counts. Exact
/// integer counts matter here because the health policies run significance tests on them; extrapolated fractional
/// counts from <c>increase()</c> would overstate small samples.
/// </remarks>
public sealed class CounterWindow
{
    private readonly TimeSpan _length;
    private readonly List<CounterSnapshot> _snapshots = [];

    public CounterWindow(TimeSpan length)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(length, TimeSpan.Zero);
        _length = length;
    }

    public int SnapshotCount => _snapshots.Count;

    /// <summary>Adds a snapshot. Snapshots that are not newer than the last one are ignored (clock skew, retries).</summary>
    public void Add(CounterSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (_snapshots.Count > 0 && snapshot.At <= _snapshots[^1].At)
        {
            return;
        }

        _snapshots.Add(snapshot);

        // Keep the newest snapshot that is at or before the window start as the baseline, so the window covers its
        // full length instead of shrinking by up to one scrape interval.
        var windowStart = snapshot.At - _length;
        var firstInside = _snapshots.FindIndex(s => s.At > windowStart);
        var keepFrom = Math.Max(0, firstInside - 1);
        if (keepFrom > 0)
        {
            _snapshots.RemoveRange(0, keepFrom);
        }
    }

    /// <summary>Forgets everything, e.g. after the flag changed hands and old traffic no longer describes it.</summary>
    public void Clear() => _snapshots.Clear();

    public WindowStats Compute()
    {
        if (_snapshots.Count < 2)
        {
            return WindowStats.Empty;
        }

        var perVariant = new Dictionary<string, VariantCounts>(StringComparer.Ordinal);
        for (var i = 1; i < _snapshots.Count; i++)
        {
            var previous = _snapshots[i - 1].Series;
            foreach (var (key, current) in _snapshots[i].Series)
            {
                var before = previous.TryGetValue(key, out var p) ? p : VariantCounts.Zero;
                var increment = Increment(before, current);
                perVariant[key.Variant] = perVariant.TryGetValue(key.Variant, out var sum) ? sum + increment : increment;
            }
        }

        return new WindowStats(perVariant, _snapshots[^1].At - _snapshots[0].At);
    }

    /// <summary>Counter increment between two reads of one series, with reset handling per counter.</summary>
    internal static VariantCounts Increment(VariantCounts before, VariantCounts after)
    {
        // Requests and errors are separate Prometheus series and are reset together only in theory; each is treated
        // on its own so a torn read can never produce a negative count.
        var requests = after.Requests >= before.Requests ? after.Requests - before.Requests : after.Requests;
        var errors = after.Errors >= before.Errors ? after.Errors - before.Errors : after.Errors;
        return new VariantCounts(requests, Math.Min(errors, requests));
    }
}
