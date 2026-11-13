using FlagsRollout.Core.Analysis;
using FlagsRollout.Core.Rollout;

namespace FlagsRollout.UnitTests;

public sealed class RolloutEngineTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 13, 9, 0, 0, TimeSpan.Zero);
    private static readonly RolloutPlan Plan = new([10, 25, 50], TimeSpan.FromSeconds(60), breachesToRollback: 2);

    private static readonly Verdict Healthy = Verdict.Healthy("ok");
    private static readonly Verdict Unhealthy = Verdict.Unhealthy("bad");
    private static readonly Verdict Unknown = Verdict.Inconclusive("?");

    [Fact]
    public void Healthy_before_the_bake_time_holds_the_step()
    {
        var state = RolloutState.Start(Plan, 10, T0);

        var decision = RolloutEngine.Decide(Plan, state, Healthy, T0.AddSeconds(59));

        decision.Action.Should().BeOfType<RolloutAction.None>();
        decision.State.Percent.Should().Be(10);
    }

    [Fact]
    public void Healthy_after_the_bake_time_advances_one_step()
    {
        var state = RolloutState.Start(Plan, 10, T0);

        var decision = RolloutEngine.Decide(Plan, state, Healthy, T0.AddSeconds(60));

        decision.Action.Should().BeEquivalentTo(new RolloutAction.SetPercent(25, decision.State.Reason!));
        decision.State.Should().Match<RolloutState>(s => s.StepIndex == 1 && s.Percent == 25 && s.StepStartedAt == T0.AddSeconds(60));
    }

    [Fact]
    public void Healthy_on_the_last_step_promotes_to_100()
    {
        var state = RolloutState.Start(Plan, 50, T0);

        var decision = RolloutEngine.Decide(Plan, state, Healthy, T0.AddMinutes(1));

        decision.Action.Should().BeOfType<RolloutAction.SetPercent>().Which.Percent.Should().Be(100);
        decision.State.Phase.Should().Be(RolloutPhase.Promoted);
    }

    [Fact]
    public void A_single_breach_does_not_roll_back_two_in_a_row_do()
    {
        var state = RolloutState.Start(Plan, 25, T0);

        var first = RolloutEngine.Decide(Plan, state, Unhealthy, T0.AddSeconds(5));
        var second = RolloutEngine.Decide(Plan, first.State, Unhealthy, T0.AddSeconds(10));

        first.Action.Should().BeOfType<RolloutAction.None>();
        first.State.BreachStreak.Should().Be(1);
        second.Action.Should().BeOfType<RolloutAction.Rollback>().Which.Reason.Should().Contain("2 unhealthy checks in a row at 25%");
        second.State.Should().Match<RolloutState>(s => s.Phase == RolloutPhase.RolledBack && s.Percent == 0);
    }

    [Fact]
    public void A_healthy_check_resets_the_breach_streak()
    {
        var state = RolloutState.Start(Plan, 25, T0);
        state = RolloutEngine.Decide(Plan, state, Unhealthy, T0.AddSeconds(5)).State;
        state = RolloutEngine.Decide(Plan, state, Healthy, T0.AddSeconds(10)).State;

        var decision = RolloutEngine.Decide(Plan, state, Unhealthy, T0.AddSeconds(15));

        decision.Action.Should().BeOfType<RolloutAction.None>();
    }

    [Fact]
    public void Inconclusive_keeps_the_streak_and_never_advances()
    {
        var state = RolloutState.Start(Plan, 25, T0);
        state = RolloutEngine.Decide(Plan, state, Unhealthy, T0.AddSeconds(5)).State;

        var waiting = RolloutEngine.Decide(Plan, state, Unknown, T0.AddMinutes(10));
        var thenBad = RolloutEngine.Decide(Plan, waiting.State, Unhealthy, T0.AddMinutes(11));

        waiting.Action.Should().BeOfType<RolloutAction.None>();
        waiting.State.Percent.Should().Be(25);
        thenBad.Action.Should().BeOfType<RolloutAction.Rollback>();
    }

    [Theory]
    [InlineData(RolloutPhase.Promoted)]
    [InlineData(RolloutPhase.RolledBack)]
    public void Terminal_phases_ignore_verdicts(RolloutPhase phase)
    {
        var state = RolloutState.Start(Plan, 25, T0) with { Phase = phase };

        RolloutEngine.Decide(Plan, state, Unhealthy, T0.AddHours(1)).Action.Should().BeOfType<RolloutAction.None>();
        RolloutEngine.Decide(Plan, state, Healthy, T0.AddHours(1)).Action.Should().BeOfType<RolloutAction.None>();
    }

    [Theory]
    [InlineData(0, RolloutPhase.RolledBack, 0)]
    [InlineData(5, RolloutPhase.Progressing, 0)]
    [InlineData(10, RolloutPhase.Progressing, 0)]
    [InlineData(30, RolloutPhase.Progressing, 1)]
    [InlineData(50, RolloutPhase.Progressing, 2)]
    [InlineData(100, RolloutPhase.Promoted, 2)]
    public void Start_derives_the_state_from_the_weight_in_the_file(int percent, RolloutPhase phase, int stepIndex)
    {
        var state = RolloutState.Start(Plan, percent, T0);

        state.Phase.Should().Be(phase);
        state.StepIndex.Should().Be(stepIndex);
    }

    [Fact]
    public void A_hand_edited_weight_between_steps_advances_to_the_next_step_not_past_it()
    {
        var state = RolloutState.Start(Plan, 30, T0);

        RolloutEngine.Decide(Plan, state, Healthy, T0.AddMinutes(1)).State.Percent.Should().Be(50);
    }

    [Theory]
    [InlineData(new int[0])]
    [InlineData(new[] { 0, 10 })]
    [InlineData(new[] { 10, 10 })]
    [InlineData(new[] { 50, 25 })]
    [InlineData(new[] { 101 })]
    public void Plans_reject_invalid_steps(int[] steps)
    {
        FluentActions.Invoking(() => new RolloutPlan(steps, TimeSpan.Zero, 1)).Should().Throw<ArgumentException>();
    }
}
