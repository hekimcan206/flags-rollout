using System.Globalization;
using System.Text.Json;
using FlagsRollout.Core.Analysis;

namespace FlagsRollout.Controller;

/// <summary>Reads the cumulative request counters at one instant.</summary>
public interface ICounterSource
{
    Task<CounterSnapshot> ReadAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Reads <c>rollout_requests_total</c> from the Prometheus HTTP API as raw cumulative values per instance, variant
/// and outcome. Deltas are computed by <see cref="CounterWindow"/>, not by PromQL, so the counts stay exact integers.
/// </summary>
public sealed class PrometheusCounterSource(HttpClient http, ControllerOptions options) : ICounterSource
{
    public const string MetricName = "rollout_requests_total";

    public string Query { get; } = string.Create(
        CultureInfo.InvariantCulture,
        // FlagKey is validated against ^[A-Za-z0-9._-]+$, so it cannot break out of the string literal.
        $"sum by (instance, feature_flag_result_variant, outcome) ({MetricName}{{feature_flag_key=\"{options.FlagKey}\"}})");

    public async Task<CounterSnapshot> ReadAsync(CancellationToken cancellationToken)
    {
        var uri = new Uri($"api/v1/query?query={Uri.EscapeDataString(Query)}", UriKind.Relative);
        using var response = await http.GetAsync(uri, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var json = await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken);
        return Parse(json.RootElement, DateTimeOffset.UtcNow);
    }

    /// <summary>Parses an instant-vector response. <paramref name="fallbackTime"/> is used when the vector is empty.</summary>
    public static CounterSnapshot Parse(JsonElement root, DateTimeOffset fallbackTime)
    {
        if (root.GetProperty("status").GetString() != "success")
        {
            throw new InvalidOperationException($"Prometheus query failed: {root.GetRawText()}");
        }

        var data = root.GetProperty("data");
        if (data.GetProperty("resultType").GetString() != "vector")
        {
            throw new InvalidOperationException("Expected an instant vector from Prometheus.");
        }

        var series = new Dictionary<SeriesKey, VariantCounts>();
        DateTimeOffset? at = null;
        foreach (var sample in data.GetProperty("result").EnumerateArray())
        {
            var labels = sample.GetProperty("metric");
            var instance = labels.TryGetProperty("instance", out var i) ? i.GetString() ?? string.Empty : string.Empty;
            var variant = labels.TryGetProperty("feature_flag_result_variant", out var v) ? v.GetString() ?? string.Empty : string.Empty;
            var isError = labels.TryGetProperty("outcome", out var o) && o.GetString() == "error";

            var value = sample.GetProperty("value");
            // All samples of one instant query share the evaluation timestamp: seconds as a float.
            at ??= DateTimeOffset.FromUnixTimeMilliseconds((long)Math.Round(value[0].GetDouble() * 1000));
            var count = (long)Math.Round(double.Parse(value[1].GetString()!, NumberStyles.Float, CultureInfo.InvariantCulture));

            var key = new SeriesKey(instance, variant);
            var current = series.TryGetValue(key, out var c) ? c : VariantCounts.Zero;
            series[key] = current + new VariantCounts(count, isError ? count : 0);
        }

        return new CounterSnapshot(at ?? fallbackTime, series);
    }
}
