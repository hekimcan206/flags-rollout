using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace FlagsRollout.Core.Flags;

/// <summary>
/// A flagd flag-definition file (https://flagd.dev/schema/v0/flags.json) and the only three edits the controller
/// makes to it: set the canary percentage, promote, and roll back.
/// </summary>
/// <remarks>
/// The document is edited as a JSON tree rather than mapped to classes so that everything the controller does not
/// own (other flags, <c>$evaluators</c>, unknown future properties, key order) survives a rewrite unchanged. A typed
/// model would silently drop what it does not know about, in a file humans also edit.
/// </remarks>
public sealed class FlagDocument
{
    public const string MetadataPhase = "rolloutPhase";
    public const string MetadataReason = "rolloutReason";
    public const string MetadataUpdatedAt = "rolloutUpdatedAt";
    public const int MaxReasonLength = 300;

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        IndentSize = 2,
        // Same bytes on every OS: the content hash is the concurrency token, and the file is diffed in reviews.
        NewLine = "\n",
        // Flag files are read by humans; keep '<', '>' and non-ASCII readable. The output is never embedded in HTML.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly JsonDocumentOptions ReadOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = false,
    };

    private readonly JsonObject _root;

    private FlagDocument(JsonObject root) => _root = root;

    public static FlagDocument Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(json, documentOptions: ReadOptions);
        }
        catch (JsonException ex)
        {
            throw new FlagDocumentException($"Flag file is not valid JSON: {ex.Message}", ex);
        }

        if (node is not JsonObject root || root["flags"] is not JsonObject)
        {
            throw new FlagDocumentException("Flag file must be an object with a \"flags\" object.");
        }

        return new FlagDocument(root);
    }

    public string ToJson() => _root.ToJsonString(WriteOptions) + "\n";

    public IReadOnlyCollection<string> FlagKeys => [.. Flags.Select(p => p.Key)];

    private JsonObject Flags => (JsonObject)_root["flags"]!;

    /// <summary>
    /// The share of evaluations that resolve to the canary variant, in percent: from the <c>fractional</c> weights
    /// when the flag targets, or 100/0 from the default variant when it does not.
    /// </summary>
    public int GetCanaryPercent(CanaryFlag flag)
    {
        ArgumentNullException.ThrowIfNull(flag);
        var definition = Require(flag);
        var weights = ReadFractionalWeights(definition);
        if (weights is null)
        {
            return string.Equals(DefaultVariant(definition), flag.CanaryVariant, StringComparison.Ordinal) ? 100 : 0;
        }

        var total = weights.Sum(w => w.Weight);
        if (total <= 0)
        {
            throw new FlagDocumentException($"Flag '{flag.Key}' has fractional weights that sum to {total}.");
        }

        var canary = weights.Where(w => w.Variant == flag.CanaryVariant).Sum(w => w.Weight);
        // Round down: reporting 9.9% as 10% would let the controller skip the real 10% step.
        return (int)Math.Floor(canary * 100.0 / total);
    }

    public RolloutMetadata? GetRolloutMetadata(CanaryFlag flag)
    {
        ArgumentNullException.ThrowIfNull(flag);
        if (Require(flag)["metadata"] is not JsonObject metadata || StringOrNull(metadata[MetadataPhase]) is not { } phase)
        {
            return null;
        }

        var updatedAt = DateTimeOffset.TryParse(
            StringOrNull(metadata[MetadataUpdatedAt]),
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal,
            out var at) ? at : DateTimeOffset.MinValue;
        return new RolloutMetadata(phase, StringOrNull(metadata[MetadataReason]) ?? string.Empty, updatedAt);
    }

    /// <summary>
    /// Serves <paramref name="percent"/>% of targeting keys the canary variant. The bucketing expression already in
    /// the file is kept, so raising the percentage only adds users: everyone on the canary at 10% stays on it at 25%.
    /// </summary>
    public FlagDocument WithCanaryPercent(CanaryFlag flag, int percent, string reason, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(flag);
        ArgumentNullException.ThrowIfNull(reason);
        ArgumentOutOfRangeException.ThrowIfLessThan(percent, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(percent, 100);
        if (percent == 100)
        {
            return WithDefaultVariant(flag, flag.CanaryVariant, "promoted", reason, at);
        }

        var copy = Clone();
        var definition = copy.Require(flag);
        var bucketBy = ExistingBucketExpression(definition) ?? DefaultBucketExpression();
        definition["targeting"] = new JsonObject
        {
            // The canary bucket comes first: flagd assigns buckets in order, so growing its weight extends the same
            // range of hash values instead of reshuffling who is in it.
            ["fractional"] = new JsonArray(
                bucketBy,
                new JsonArray(flag.CanaryVariant, percent),
                new JsonArray(flag.BaselineVariant, 100 - percent)),
        };
        definition["defaultVariant"] = flag.BaselineVariant;
        WriteMetadata(definition, "progressing", reason, at);
        return copy;
    }

    /// <summary>
    /// Serves the baseline variant to everyone and records why. Targeting is removed, not just reweighted, so that
    /// nothing short of a deliberate edit can bring the canary back.
    /// </summary>
    public FlagDocument WithRollback(CanaryFlag flag, string reason, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(flag);
        ArgumentNullException.ThrowIfNull(reason);
        return WithDefaultVariant(flag, flag.BaselineVariant, "rolled-back", reason, at);
    }

    private FlagDocument WithDefaultVariant(CanaryFlag flag, string variant, string phase, string reason, DateTimeOffset at)
    {
        var copy = Clone();
        var definition = copy.Require(flag);
        definition.Remove("targeting");
        definition["defaultVariant"] = variant;
        WriteMetadata(definition, phase, reason, at);
        return copy;
    }

    private static void WriteMetadata(JsonObject definition, string phase, string reason, DateTimeOffset at)
    {
        if (definition["metadata"] is not JsonObject metadata)
        {
            metadata = [];
            definition["metadata"] = metadata;
        }

        metadata[MetadataPhase] = phase;
        // flagd metadata values are strings, numbers or booleans; keep the reason short enough to read in a log line.
        metadata[MetadataReason] = reason.Length <= MaxReasonLength ? reason : reason[..MaxReasonLength];
        metadata[MetadataUpdatedAt] = at.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
    }

    private JsonObject Require(CanaryFlag flag)
    {
        if (Flags[flag.Key] is not JsonObject definition)
        {
            throw new FlagDocumentException($"Flag '{flag.Key}' is not defined in the flag file.");
        }

        if (definition["variants"] is not JsonObject variants ||
            !variants.ContainsKey(flag.CanaryVariant) ||
            !variants.ContainsKey(flag.BaselineVariant))
        {
            throw new FlagDocumentException(
                $"Flag '{flag.Key}' must define both variants '{flag.CanaryVariant}' and '{flag.BaselineVariant}'.");
        }

        if (StringOrNull(definition["state"]) is { } state && state != "ENABLED")
        {
            throw new FlagDocumentException($"Flag '{flag.Key}' is {state}; the controller only manages ENABLED flags.");
        }

        return definition;
    }

    private static string? StringOrNull(JsonNode? node) =>
        node?.GetValueKind() == JsonValueKind.String ? node.GetValue<string>() : null;

    private static string? DefaultVariant(JsonObject definition) => StringOrNull(definition["defaultVariant"]);

    private static List<(string Variant, double Weight)>? ReadFractionalWeights(JsonObject definition)
    {
        if (definition["targeting"] is not JsonObject targeting || targeting.Count == 0)
        {
            return null;
        }

        if (targeting.Count != 1 || targeting["fractional"] is not JsonArray fractional)
        {
            // Anything richer (if/else trees, segments) is a human's targeting design; the controller refuses to
            // reinterpret it rather than guess which branch is "the canary".
            throw new FlagDocumentException("Only a top-level \"fractional\" targeting rule can be managed by the controller.");
        }

        var weights = new List<(string, double)>();
        foreach (var item in fractional)
        {
            if (item is JsonArray { Count: >= 1 } bucket && StringOrNull(bucket[0]) is { } variant)
            {
                // flagd: a bucket without a weight has weight 1.
                // ToJsonString + parse reads the number whether the node was parsed from text or built in memory as an int.
                var weight = bucket.Count >= 2 && bucket[1]?.GetValueKind() == JsonValueKind.Number
                    ? double.Parse(bucket[1]!.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture)
                    : 1;
                weights.Add((variant, weight));
            }
        }

        return weights;
    }

    private static JsonNode? ExistingBucketExpression(JsonObject definition) =>
        definition["targeting"] is JsonObject targeting &&
        targeting["fractional"] is JsonArray { Count: > 0 } fractional &&
        fractional[0] is JsonObject expression
            ? expression.DeepClone()
            : null;

    // Hash the flag key with the targeting key, as flagd does when no expression is given, but explicitly: the
    // assignment then does not depend on a flagd default that could change between versions.
    private static JsonObject DefaultBucketExpression() => new()
    {
        ["cat"] = new JsonArray(new JsonObject { ["var"] = "$flagd.flagKey" }, new JsonObject { ["var"] = "targetingKey" }),
    };

    private FlagDocument Clone() => new((JsonObject)_root.DeepClone());
}
