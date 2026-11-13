using System.Globalization;
using FlagsRollout.Core.Analysis;

namespace FlagsRollout.Core.Rollout;

/// <summary>A decision: the next state and the change to make to the flag.</summary>
public sealed record RolloutDecision(RolloutState State, RolloutAction Action);

/// <summary>
/// The rollout state machine. Pure: the same state, verdict and time always give the same decision, which is what
/// lets the lab replay thousands of simulated canaries and the tests enumerate every transition.
/// </summary>
public static class RolloutEngine
{
    public static RolloutDecision Decide(RolloutPlan plan, RolloutState state, Verdict verdict, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(verdict);

        if (state.Phase != RolloutPhase.Progressing)
        {
            return new RolloutDecision(state, RolloutAction.NoChange);
        }

        switch (verdict.Kind)
        {
            case VerdictKind.Unhealthy:
                var streak = state.BreachStreak + 1;
                if (streak < plan.BreachesToRollback)
                {
                    return new RolloutDecision(state with { BreachStreak = streak, Reason = verdict.Reason }, RolloutAction.NoChange);
                }

                var reason = string.Create(
                    CultureInfo.InvariantCulture,
                    $"{streak} unhealthy checks in a row at {state.Percent}%: {verdict.Reason}");
                return new RolloutDecision(
                    state with { Phase = RolloutPhase.RolledBack, Percent = 0, BreachStreak = streak, StepStartedAt = now, Reason = reason },
                    new RolloutAction.Rollback(reason));

            case VerdictKind.Healthy:
                var healthy = state with { BreachStreak = 0, Reason = verdict.Reason };
                if (now - state.StepStartedAt < plan.BakeTime)
                {
                    return new RolloutDecision(healthy, RolloutAction.NoChange);
                }

                var isLastStep = state.StepIndex >= plan.Steps.Count - 1;
                var nextPercent = isLastStep ? 100 : plan.Steps[state.StepIndex + 1];
                var why = string.Create(
                    CultureInfo.InvariantCulture,
                    $"healthy for {plan.BakeTime.TotalSeconds:0}s at {state.Percent}%: {verdict.Reason}");
                var next = healthy with
                {
                    Phase = nextPercent >= 100 ? RolloutPhase.Promoted : RolloutPhase.Progressing,
                    StepIndex = isLastStep ? state.StepIndex : state.StepIndex + 1,
                    Percent = nextPercent,
                    StepStartedAt = now,
                    Reason = why,
                };
                return new RolloutDecision(next, new RolloutAction.SetPercent(nextPercent, why));

            default:
                // Inconclusive neither resets the breach streak nor lets the step advance: a canary that stops receiving
                // traffic must not be promoted for "not failing", and a gap in data must not erase earlier failures.
                return new RolloutDecision(state with { Reason = verdict.Reason }, RolloutAction.NoChange);
        }
    }
}
