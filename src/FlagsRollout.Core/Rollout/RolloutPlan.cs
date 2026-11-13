namespace FlagsRollout.Core.Rollout;

/// <summary>
/// How a canary advances: through <see cref="Steps"/> (percent of traffic on the new variant), staying healthy for
/// <see cref="BakeTime"/> at each, and rolled back after <see cref="BreachesToRollback"/> unhealthy checks in a row.
/// </summary>
public sealed record RolloutPlan
{
    public RolloutPlan(IReadOnlyList<int> steps, TimeSpan bakeTime, int breachesToRollback)
    {
        ArgumentNullException.ThrowIfNull(steps);
        if (steps.Count == 0)
        {
            throw new ArgumentException("A rollout plan needs at least one step.", nameof(steps));
        }

        for (var i = 0; i < steps.Count; i++)
        {
            if (steps[i] is <= 0 or > 100)
            {
                throw new ArgumentException($"Step {steps[i]} is outside 1..100.", nameof(steps));
            }

            if (i > 0 && steps[i] <= steps[i - 1])
            {
                throw new ArgumentException("Steps must be strictly increasing.", nameof(steps));
            }
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(bakeTime, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(breachesToRollback, 1);

        Steps = steps;
        BakeTime = bakeTime;
        BreachesToRollback = breachesToRollback;
    }

    public IReadOnlyList<int> Steps { get; }

    public TimeSpan BakeTime { get; }

    public int BreachesToRollback { get; }

    /// <summary>
    /// The step index that matches a weight found in the flag file: the highest step not above it, so a canary that
    /// was edited by hand to an in-between weight is never jumped forward past what an operator chose.
    /// </summary>
    public int StepIndexFor(int percent)
    {
        var index = 0;
        for (var i = 0; i < Steps.Count; i++)
        {
            if (Steps[i] <= percent)
            {
                index = i;
            }
        }

        return index;
    }
}
