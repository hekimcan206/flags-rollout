using System.ComponentModel.DataAnnotations;
using FlagsRollout.Core.Analysis;
using FlagsRollout.Core.Flags;
using FlagsRollout.Core.Rollout;

namespace FlagsRollout.Controller;

public enum PolicyKind
{
    Threshold,
    Relative,
    Combined,
}

public sealed class ControllerOptions : IValidatableObject
{
    public const string Section = "Rollout";

    /// <summary>The flagd flag file this controller owns the canary flag in (shared with flagd's file sync).</summary>
    [Required]
    public string FlagFile { get; set; } = "flags/flags.json";

    /// <summary>Restricted to characters that are safe inside a PromQL string literal and a file name.</summary>
    [Required, RegularExpression("^[A-Za-z0-9._-]{1,128}$")]
    public string FlagKey { get; set; } = "new-pricing-engine";

    [Required, RegularExpression("^[A-Za-z0-9._-]{1,64}$")]
    public string CanaryVariant { get; set; } = "on";

    [Required, RegularExpression("^[A-Za-z0-9._-]{1,64}$")]
    public string BaselineVariant { get; set; } = "off";

    [Required, Url]
    public string PrometheusUrl { get; set; } = "http://localhost:9090";

    [Range(typeof(TimeSpan), "00:00:00.200", "00:10:00")]
    public TimeSpan TickInterval { get; set; } = TimeSpan.FromSeconds(5);

    [Range(typeof(TimeSpan), "00:00:01", "01:00:00")]
    public TimeSpan Window { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>Empty by default: the configuration binder appends to a pre-filled array instead of replacing it.</summary>
    [MinLength(1)]
    public int[] Steps { get; set; } = [];

    [Range(typeof(TimeSpan), "00:00:00", "1.00:00:00")]
    public TimeSpan BakeTime { get; set; } = TimeSpan.FromMinutes(2);

    [Range(1, 100)]
    public int BreachesToRollback { get; set; } = 2;

    public PolicyKind Policy { get; set; } = PolicyKind.Combined;

    [Range(0.0, 1.0)]
    public double MaxErrorRate { get; set; } = 0.05;

    [Range(1, 1_000_000)]
    public long MinRequests { get; set; } = 100;

    [Range(0.0, 10.0)]
    public double ZCritical { get; set; } = 3.09;

    [Range(0.0, 1.0)]
    public double MinAbsoluteIncrease { get; set; } = 0.01;

    /// <summary>Append-only JSON-lines record of every decision that changed the flag. Empty disables the file.</summary>
    public string AuditLog { get; set; } = string.Empty;

    public CanaryFlag Flag => new(FlagKey, CanaryVariant, BaselineVariant);

    public RolloutPlan Plan() => new(Steps, BakeTime, BreachesToRollback);

    public IHealthPolicy BuildPolicy()
    {
        var threshold = new ThresholdPolicy(MaxErrorRate, MinRequests);
        var relative = new RelativePolicy(ZCritical, MinAbsoluteIncrease, MinRequests);
        return Policy switch
        {
            PolicyKind.Threshold => threshold,
            PolicyKind.Relative => relative,
            _ => new CombinedPolicy(threshold, relative),
        };
    }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        ValidationResult? error = null;
        try
        {
            _ = Plan();
        }
        catch (ArgumentException ex)
        {
            error = new ValidationResult(ex.Message, [nameof(Steps)]);
        }

        if (error is not null)
        {
            yield return error;
        }

        if (CanaryVariant == BaselineVariant)
        {
            yield return new ValidationResult("Canary and baseline variants must differ.", [nameof(CanaryVariant)]);
        }

        if (Window < TickInterval * 2)
        {
            yield return new ValidationResult("The window must span at least two ticks to contain an increment.", [nameof(Window)]);
        }
    }
}
