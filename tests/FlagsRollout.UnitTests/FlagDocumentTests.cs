using FlagsRollout.Core.Flags;

namespace FlagsRollout.UnitTests;

public sealed class FlagDocumentTests
{
    private static readonly CanaryFlag Flag = new("f", "on", "off");
    private static readonly DateTimeOffset At = new(2026, 11, 13, 9, 30, 0, TimeSpan.Zero);

    private static string FlagJson(string body) =>
        $$"""{ "flags": { "f": { "state": "ENABLED", "variants": { "on": true, "off": false }, {{body}} } } }""";

    [Theory]
    [InlineData("\"defaultVariant\": \"off\"", 0)]
    [InlineData("\"defaultVariant\": \"on\"", 100)]
    [InlineData("\"defaultVariant\": \"off\", \"targeting\": {}", 0)]
    [InlineData("\"defaultVariant\": \"off\", \"targeting\": { \"fractional\": [ [\"on\", 10], [\"off\", 90] ] }", 10)]
    [InlineData("\"defaultVariant\": \"off\", \"targeting\": { \"fractional\": [ { \"var\": \"k\" }, [\"on\", 1], [\"off\", 3] ] }", 25)]
    [InlineData("\"defaultVariant\": \"off\", \"targeting\": { \"fractional\": [ [\"on\"], [\"off\"] ] }", 50)]
    [InlineData("\"defaultVariant\": \"off\", \"targeting\": { \"fractional\": [ [\"on\", 1], [\"off\", 2] ] }", 33)]
    public void Reads_the_canary_percentage(string body, int expected) =>
        FlagDocument.Parse(FlagJson(body)).GetCanaryPercent(Flag).Should().Be(expected);

    [Fact]
    public void Advancing_keeps_the_canary_bucket_first_and_weights_summing_to_100()
    {
        var doc = FlagDocument.Parse(FlagJson("\"defaultVariant\": \"off\""));

        var advanced = doc.WithCanaryPercent(Flag, 37, "r", At);

        advanced.GetCanaryPercent(Flag).Should().Be(37);
        PropertyTests.FirstBucket(advanced).Should().Be("on");
        advanced.ToJson().Should().MatchRegex("\"off\",\\s+63");
        advanced.GetRolloutMetadata(Flag)!.Phase.Should().Be("progressing");
        advanced.ToJson().Should().NotContain("\r", "the file must be byte-identical on every OS; its hash is the concurrency token");
    }

    [Fact]
    public void Rewrites_do_not_mutate_the_original_document()
    {
        var doc = FlagDocument.Parse(FlagJson("\"defaultVariant\": \"off\""));
        var before = doc.ToJson();

        _ = doc.WithCanaryPercent(Flag, 10, "r", At);
        _ = doc.WithRollback(Flag, "r", At);

        doc.ToJson().Should().Be(before);
    }

    [Fact]
    public void Rollback_removes_targeting_and_records_why()
    {
        var doc = FlagDocument.Parse(FlagJson("\"defaultVariant\": \"off\", \"targeting\": { \"fractional\": [ [\"on\", 50], [\"off\", 50] ] }"));

        var rolledBack = doc.WithRollback(Flag, "error rate too high", At);

        rolledBack.GetCanaryPercent(Flag).Should().Be(0);
        rolledBack.ToJson().Should().NotContain("targeting");
        rolledBack.GetRolloutMetadata(Flag).Should().Be(new RolloutMetadata("rolled-back", "error rate too high", At));
    }

    [Fact]
    public void Long_reasons_are_truncated_in_metadata()
    {
        var doc = FlagDocument.Parse(FlagJson("\"defaultVariant\": \"off\""));

        var rolledBack = doc.WithRollback(Flag, new string('x', 1000), At);

        rolledBack.GetRolloutMetadata(Flag)!.Reason.Should().HaveLength(FlagDocument.MaxReasonLength);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{ \"flags\": [] }")]
    public void Rejects_files_that_are_not_flag_documents(string json)
    {
        var parse = () => FlagDocument.Parse(json);
        parse.Should().Throw<FlagDocumentException>();
    }

    [Fact]
    public void Refuses_unknown_flags_missing_variants_and_disabled_flags()
    {
        var missing = FlagDocument.Parse("""{ "flags": {} }""");
        var noVariant = FlagDocument.Parse("""{ "flags": { "f": { "state": "ENABLED", "variants": { "on": true }, "defaultVariant": "on" } } }""");
        var disabled = FlagDocument.Parse("""{ "flags": { "f": { "state": "DISABLED", "variants": { "on": true, "off": false }, "defaultVariant": "off" } } }""");

        FluentActions.Invoking(() => missing.GetCanaryPercent(Flag)).Should().Throw<FlagDocumentException>().WithMessage("*not defined*");
        FluentActions.Invoking(() => noVariant.WithRollback(Flag, "r", At)).Should().Throw<FlagDocumentException>().WithMessage("*both variants*");
        FluentActions.Invoking(() => disabled.WithCanaryPercent(Flag, 10, "r", At)).Should().Throw<FlagDocumentException>().WithMessage("*DISABLED*");
    }

    [Fact]
    public void Refuses_to_reinterpret_targeting_it_does_not_own()
    {
        var doc = FlagDocument.Parse(FlagJson("\"defaultVariant\": \"off\", \"targeting\": { \"if\": [ true, \"on\", \"off\" ] }"));

        FluentActions.Invoking(() => doc.GetCanaryPercent(Flag)).Should().Throw<FlagDocumentException>().WithMessage("*fractional*");
    }

    [Fact]
    public void Missing_metadata_reads_as_null()
    {
        FlagDocument.Parse(FlagJson("\"defaultVariant\": \"off\"")).GetRolloutMetadata(Flag).Should().BeNull();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    public void Percent_must_be_between_1_and_100(int percent)
    {
        var doc = FlagDocument.Parse(FlagJson("\"defaultVariant\": \"off\""));
        FluentActions.Invoking(() => doc.WithCanaryPercent(Flag, percent, "r", At)).Should().Throw<ArgumentOutOfRangeException>();
    }
}
