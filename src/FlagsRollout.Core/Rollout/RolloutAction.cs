namespace FlagsRollout.Core.Rollout;

/// <summary>What the controller must do to the flag after a decision.</summary>
public abstract record RolloutAction
{
    private RolloutAction()
    {
    }

    /// <summary>Leave the flag as it is.</summary>
    public sealed record None : RolloutAction;

    /// <summary>Move the canary to a new percentage (100 means promote).</summary>
    public sealed record SetPercent(int Percent, string Reason) : RolloutAction;

    /// <summary>Turn the new variant off for everyone.</summary>
    public sealed record Rollback(string Reason) : RolloutAction;

    public static RolloutAction NoChange { get; } = new None();
}
