using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace FlagsRollout.DemoApi;

/// <summary>
/// The one metric the rollout controller reads: requests per flag variant and outcome.
/// </summary>
/// <remarks>
/// Attribute names follow the OpenTelemetry feature-flag semantic conventions (<c>feature_flag.key</c>,
/// <c>feature_flag.result.variant</c>). Prometheus' OTLP receiver turns the instrument into
/// <c>rollout_requests_total{feature_flag_key, feature_flag_result_variant, outcome}</c>.
/// Counting by the variant that was actually served (not the one configured) is the point: it attributes every
/// failure to the code path that produced it.
/// </remarks>
public sealed class RolloutMetrics
{
    public const string MeterName = "FlagsRollout.DemoApi";

    private readonly Counter<long> _requests;

    public RolloutMetrics(IMeterFactory meterFactory)
    {
        ArgumentNullException.ThrowIfNull(meterFactory);
        var meter = meterFactory.Create(MeterName);
        _requests = meter.CreateCounter<long>(
            "rollout.requests",
            unit: "{request}",
            description: "Requests served, by feature flag variant and outcome.");
    }

    public void Record(string flagKey, string variant, bool success)
    {
        var tags = new TagList
        {
            { "feature_flag.key", flagKey },
            { "feature_flag.result.variant", variant },
            { "outcome", success ? "success" : "error" },
        };
        _requests.Add(1, tags);
    }
}
