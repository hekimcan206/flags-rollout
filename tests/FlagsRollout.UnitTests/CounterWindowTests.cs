using FlagsRollout.Core.Analysis;

namespace FlagsRollout.UnitTests;

public sealed class CounterWindowTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 13, 9, 0, 0, TimeSpan.Zero);

    internal static CounterSnapshot Snap(int second, params (string Instance, string Variant, long Requests, long Errors)[] series) =>
        new(T0.AddSeconds(second), series.ToDictionary(s => new SeriesKey(s.Instance, s.Variant), s => new VariantCounts(s.Requests, s.Errors)));

    [Fact]
    public void Needs_two_snapshots_for_an_increment()
    {
        var window = new CounterWindow(TimeSpan.FromSeconds(30));
        window.Add(Snap(0, ("a", "on", 100, 5)));

        window.Compute().Should().BeSameAs(WindowStats.Empty);
    }

    [Fact]
    public void Sums_increments_per_variant_across_instances()
    {
        var window = new CounterWindow(TimeSpan.FromSeconds(30));
        window.Add(Snap(0, ("a", "on", 100, 5), ("b", "on", 50, 0), ("a", "off", 900, 9)));
        window.Add(Snap(5, ("a", "on", 110, 6), ("b", "on", 60, 2), ("a", "off", 1000, 10)));

        var stats = window.Compute();

        stats.For("on").Should().Be(new VariantCounts(20, 3));
        stats.For("off").Should().Be(new VariantCounts(100, 1));
        stats.For("missing").Should().Be(VariantCounts.Zero);
        stats.Covered.Should().Be(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void A_counter_that_goes_down_restarted_from_zero()
    {
        var window = new CounterWindow(TimeSpan.FromSeconds(60));
        window.Add(Snap(0, ("a", "on", 500, 50)));
        window.Add(Snap(5, ("a", "on", 520, 51)));
        window.Add(Snap(10, ("a", "on", 7, 1)));   // process restarted and served 7 since
        window.Add(Snap(15, ("a", "on", 17, 1)));

        window.Compute().For("on").Should().Be(new VariantCounts(20 + 7 + 10, 1 + 1 + 0));
    }

    [Fact]
    public void A_series_that_appears_counts_from_zero()
    {
        var window = new CounterWindow(TimeSpan.FromSeconds(60));
        window.Add(Snap(0, ("a", "off", 100, 0)));
        window.Add(Snap(5, ("a", "off", 110, 0), ("a", "on", 4, 1)));

        window.Compute().For("on").Should().Be(new VariantCounts(4, 1));
    }

    [Fact]
    public void Old_snapshots_fall_out_but_one_baseline_before_the_window_is_kept()
    {
        var window = new CounterWindow(TimeSpan.FromSeconds(10));
        for (var s = 0; s <= 30; s += 5)
        {
            window.Add(Snap(s, ("a", "on", s * 10, 0)));
        }

        var stats = window.Compute();

        stats.Covered.Should().Be(TimeSpan.FromSeconds(10));
        stats.For("on").Requests.Should().Be(100);
        window.SnapshotCount.Should().Be(3);
    }

    [Fact]
    public void Out_of_order_snapshots_are_ignored()
    {
        var window = new CounterWindow(TimeSpan.FromSeconds(60));
        window.Add(Snap(10, ("a", "on", 100, 0)));
        window.Add(Snap(5, ("a", "on", 1, 0)));
        window.Add(Snap(10, ("a", "on", 1, 0)));

        window.SnapshotCount.Should().Be(1);
    }

    [Fact]
    public void Clear_forgets_history()
    {
        var window = new CounterWindow(TimeSpan.FromSeconds(60));
        window.Add(Snap(0, ("a", "on", 1, 0)));
        window.Add(Snap(5, ("a", "on", 2, 0)));

        window.Clear();

        window.Compute().Should().BeSameAs(WindowStats.Empty);
    }

    [Fact]
    public void Errors_never_exceed_requests_even_on_a_torn_read()
    {
        CounterWindow.Increment(new VariantCounts(10, 2), new VariantCounts(11, 5)).Should().Be(new VariantCounts(1, 1));
    }

    [Fact]
    public void Window_length_must_be_positive()
    {
        FluentActions.Invoking(() => new CounterWindow(TimeSpan.Zero)).Should().Throw<ArgumentOutOfRangeException>();
    }
}
