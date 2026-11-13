namespace FlagsRollout.Core.Rollout;

public enum RolloutPhase
{
    /// <summary>Canary traffic is being analysed and moved up the plan.</summary>
    Progressing,

    /// <summary>The new variant serves everyone; analysis has stopped.</summary>
    Promoted,

    /// <summary>The flag was turned off for everyone. Only a human edit of the flag file restarts the rollout.</summary>
    RolledBack,
}

/// <summary>Where a rollout is. Immutable: <see cref="RolloutEngine"/> returns a new state for every decision.</summary>
public sealed record RolloutState(
    RolloutPhase Phase,
    int StepIndex,
    int Percent,
    DateTimeOffset StepStartedAt,
    int BreachStreak,
    string? Reason)
{
    public static RolloutState Start(RolloutPlan plan, int currentPercent, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (currentPercent >= 100)
        {
            return new RolloutState(RolloutPhase.Promoted, plan.Steps.Count - 1, 100, now, 0, "flag already serves 100%");
        }

        if (currentPercent <= 0)
        {
            // Zero percent is "off", not a canary step: starting from it would be the controller turning a feature on
            // by itself. The rollout begins when a human sets the first weight in the flag file.
            return new RolloutState(RolloutPhase.RolledBack, 0, 0, now, 0, "flag serves 0%: waiting for a human to start the canary");
        }

        return new RolloutState(RolloutPhase.Progressing, plan.StepIndexFor(currentPercent), currentPercent, now, 0, null);
    }
}
