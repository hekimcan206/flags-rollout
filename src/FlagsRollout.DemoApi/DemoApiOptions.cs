using System.ComponentModel.DataAnnotations;

namespace FlagsRollout.DemoApi;

public sealed class FlagdOptions
{
    public const string Section = "Flagd";

    [Required]
    public string Host { get; set; } = "localhost";

    [Range(1, 65535)]
    public int Port { get; set; } = 8013;

    /// <summary>
    /// The RPC provider caches resolved values and drops the cache on flagd's change events. Off by default here:
    /// the lab measures flag propagation, and a cache adds a second path whose staleness would blur that number.
    /// </summary>
    public bool CacheEnabled { get; set; }

    /// <summary>How long startup waits for flagd before serving defaults (the baseline variant) and retrying.</summary>
    [Range(100, 120_000)]
    public int InitTimeoutMs { get; set; } = 10_000;
}

public sealed class CanaryOptions
{
    public const string Section = "Canary";

    [Required]
    public string FlagKey { get; set; } = "new-pricing-engine";
}

public sealed class AdminOptions
{
    public const string Section = "Admin";

    /// <summary>Shared key for the fault-injection endpoints. Empty disables them entirely.</summary>
    public string ApiKey { get; set; } = string.Empty;
}

public sealed class FaultOptions
{
    public const string Section = "Faults";

    [Range(0.0, 1.0)]
    public double CanaryErrorRate { get; set; }

    [Range(0.0, 1.0)]
    public double BaselineErrorRate { get; set; }
}
