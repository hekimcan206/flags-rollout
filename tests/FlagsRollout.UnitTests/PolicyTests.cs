using FlagsRollout.Core.Analysis;

namespace FlagsRollout.UnitTests;

public sealed class PolicyTests
{
    private static VariantCounts C(long requests, long errors) => new(requests, errors);

    [Theory]
    [InlineData(99, 50, VerdictKind.Inconclusive)]
    [InlineData(100, 5, VerdictKind.Healthy)]
    [InlineData(100, 6, VerdictKind.Unhealthy)]
    [InlineData(1000, 50, VerdictKind.Healthy)]
    public void Threshold_judges_the_canary_alone(long requests, long errors, VerdictKind expected) =>
        new ThresholdPolicy(0.05, 100).Evaluate(C(requests, errors), C(0, 0)).Kind.Should().Be(expected);

    [Fact]
    public void Threshold_without_minimum_judges_a_single_request()
    {
        var policy = new ThresholdPolicy(0.05, 0);

        policy.Evaluate(C(1, 1), default).Kind.Should().Be(VerdictKind.Unhealthy);
        policy.Evaluate(C(0, 0), default).Kind.Should().Be(VerdictKind.Inconclusive);
        policy.Name.Should().Contain("no minimum");
    }

    [Fact]
    public void Z_score_matches_a_hand_computed_value()
    {
        // p1 = 0.10 (n 1000), p2 = 0.05 (n 1000), pooled 0.075, se = sqrt(0.075*0.925*0.002) = 0.011779
        RelativePolicy.ZScore(C(1000, 100), C(1000, 50)).Should().BeApproximately(4.2448, 0.001);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(100, 100)]
    public void Z_score_is_zero_when_there_is_no_variance(long canaryErrors, long baselineErrors) =>
        RelativePolicy.ZScore(C(100, canaryErrors), C(100, baselineErrors)).Should().Be(0);

    [Fact]
    public void Relative_ignores_a_shared_incident_that_threshold_rolls_back_on()
    {
        // Both variants fail 8%: a downstream outage, not the canary.
        var canary = C(1000, 80);
        var baseline = C(3000, 240);

        new ThresholdPolicy(0.05, 100).Evaluate(canary, baseline).Kind.Should().Be(VerdictKind.Unhealthy);
        new RelativePolicy(3.09, 0.01, 100).Evaluate(canary, baseline).Kind.Should().Be(VerdictKind.Healthy);
    }

    [Fact]
    public void Relative_needs_an_effect_size_not_only_significance()
    {
        // 1.3% vs 1.0% over 200k requests each is significant (z ~ 8.9) but below the 1 point floor.
        var verdict = new RelativePolicy(3.09, 0.01, 100).Evaluate(C(200_000, 2_600), C(200_000, 2_000));

        verdict.Kind.Should().Be(VerdictKind.Healthy);
        verdict.Reason.Should().Contain("z=");
    }

    [Fact]
    public void Relative_flags_a_clearly_worse_canary()
    {
        new RelativePolicy(3.09, 0.01, 100).Evaluate(C(500, 50), C(1500, 15)).Kind.Should().Be(VerdictKind.Unhealthy);
    }

    [Fact]
    public void Relative_is_inconclusive_until_both_variants_have_traffic()
    {
        new RelativePolicy(3.09, 0.01, 100).Evaluate(C(500, 50), C(99, 0)).Kind.Should().Be(VerdictKind.Inconclusive);
    }

    [Fact]
    public void Combined_needs_both_a_breach_and_blame()
    {
        var policy = new CombinedPolicy(new ThresholdPolicy(0.05, 100), new RelativePolicy(3.09, 0.01, 100));

        policy.Evaluate(C(1000, 80), C(3000, 240)).Kind.Should().Be(VerdictKind.Healthy);     // shared incident
        policy.Evaluate(C(1000, 40), C(3000, 3)).Kind.Should().Be(VerdictKind.Healthy);       // worse but under 5%
        policy.Evaluate(C(1000, 120), C(3000, 30)).Kind.Should().Be(VerdictKind.Unhealthy);   // bad canary
        policy.Evaluate(C(50, 40), C(3000, 30)).Kind.Should().Be(VerdictKind.Inconclusive);   // too few requests
        policy.Name.Should().Contain("AND");
    }

    [Fact]
    public void Verdict_reasons_are_culture_invariant()
    {
        var previous = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("tr-TR");
            new ThresholdPolicy(0.05, 1).Evaluate(C(1000, 123), default).Reason.Should().Contain("12.30%");
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = previous;
        }
    }
}
