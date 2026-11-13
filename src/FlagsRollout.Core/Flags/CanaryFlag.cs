namespace FlagsRollout.Core.Flags;

/// <summary>
/// Identifies the flag under rollout and its two variants: <see cref="CanaryVariant"/> is the new behaviour,
/// <see cref="BaselineVariant"/> the current one.
/// </summary>
public sealed record CanaryFlag(string Key, string CanaryVariant, string BaselineVariant);

/// <summary>What the controller recorded in the flag's metadata at its last change.</summary>
public sealed record RolloutMetadata(string Phase, string Reason, DateTimeOffset UpdatedAt);

/// <summary>The flag file does not have the shape the controller is allowed to rewrite.</summary>
public sealed class FlagDocumentException : Exception
{
    public FlagDocumentException()
    {
    }

    public FlagDocumentException(string message)
        : base(message)
    {
    }

    public FlagDocumentException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
