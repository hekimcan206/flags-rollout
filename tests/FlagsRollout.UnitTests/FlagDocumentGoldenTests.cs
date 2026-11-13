using FlagsRollout.Core.Flags;

namespace FlagsRollout.UnitTests;

/// <summary>
/// Approval tests for the three rewrites the controller makes to a flagd file. The expected files are checked in
/// and reviewed like code; set UPDATE_GOLDEN=1 to regenerate them after an intended change.
/// </summary>
public sealed class FlagDocumentGoldenTests
{
    private static readonly CanaryFlag Flag = new("new-pricing-engine", "on", "off");
    private static readonly DateTimeOffset At = new(2026, 11, 13, 9, 30, 0, TimeSpan.Zero);

    public static TheoryData<string, string> Cases => new()
    {
        { "flags.input.json", "advance-25" },
        { "flags.input.json", "promote" },
        { "flags.input.json", "rollback" },
        { "flags.custom-bucketing.input.json", "advance-50" },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void Rewrite_matches_the_approved_file(string input, string rewrite)
    {
        var document = FlagDocument.Parse(File.ReadAllText(Path.Combine("Golden", input)));
        var result = rewrite switch
        {
            "advance-25" => document.WithCanaryPercent(Flag, 25, "healthy for 120s at 10%", At),
            "advance-50" => document.WithCanaryPercent(Flag, 50, "healthy for 120s at 25%", At),
            "promote" => document.WithCanaryPercent(Flag, 100, "healthy for 120s at 50%", At),
            "rollback" => document.WithRollback(Flag, "2 unhealthy checks in a row at 25%: canary 9.10% (91/1000) vs baseline 1.00% (30/3000)", At),
            _ => throw new ArgumentOutOfRangeException(nameof(rewrite)),
        };

        var name = $"{Path.GetFileNameWithoutExtension(input).Replace(".input", string.Empty, StringComparison.Ordinal)}.{rewrite}.approved.json";
        var actual = result.ToJson();
        if (Environment.GetEnvironmentVariable("UPDATE_GOLDEN") == "1")
        {
            File.WriteAllText(Path.Combine(SourceGoldenDirectory(), name), actual);
        }

        actual.ReplaceLineEndings("\n").Should().Be(File.ReadAllText(Path.Combine("Golden", name)).ReplaceLineEndings("\n"));
    }

    [Fact]
    public void Rewrites_leave_other_flags_and_evaluators_untouched()
    {
        var original = FlagDocument.Parse(File.ReadAllText(Path.Combine("Golden", "flags.input.json")));
        var rolledBack = original.WithRollback(Flag, "test", At);

        Section(rolledBack.ToJson(), "checkout-banner").Should().Be(Section(original.ToJson(), "checkout-banner"));
        Section(rolledBack.ToJson(), "$evaluators").Should().Be(Section(original.ToJson(), "$evaluators"));
    }

    private static string Section(string json, string key)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var root = doc.RootElement;
        return root.TryGetProperty(key, out var top) ? top.GetRawText() : root.GetProperty("flags").GetProperty(key).GetRawText();
    }

    private static string SourceGoldenDirectory([System.Runtime.CompilerServices.CallerFilePath] string path = "") =>
        Path.Combine(Path.GetDirectoryName(path)!, "Golden");
}
