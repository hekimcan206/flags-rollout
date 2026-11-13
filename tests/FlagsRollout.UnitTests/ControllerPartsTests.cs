using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using FlagsRollout.Controller;
using FlagsRollout.Core.Analysis;
using FlagsRollout.Core.Flags;

namespace FlagsRollout.UnitTests;

public sealed class PrometheusResponseTests
{
    [Fact]
    public void Parses_an_instant_vector_into_series_per_instance_and_variant()
    {
        const string body = """
        { "status": "success", "data": { "resultType": "vector", "result": [
          { "metric": { "instance": "api-1", "feature_flag_result_variant": "on",  "outcome": "success" }, "value": [ 1763026200.123, "95" ] },
          { "metric": { "instance": "api-1", "feature_flag_result_variant": "on",  "outcome": "error" },   "value": [ 1763026200.123, "5" ] },
          { "metric": { "instance": "api-2", "feature_flag_result_variant": "off", "outcome": "success" }, "value": [ 1763026200.123, "1e3" ] }
        ] } }
        """;
        using var json = JsonDocument.Parse(body);

        var snapshot = PrometheusCounterSource.Parse(json.RootElement, DateTimeOffset.UnixEpoch);

        snapshot.At.Should().Be(DateTimeOffset.FromUnixTimeMilliseconds(1763026200123));
        snapshot.Series[new SeriesKey("api-1", "on")].Should().Be(new VariantCounts(100, 5));
        snapshot.Series[new SeriesKey("api-2", "off")].Should().Be(new VariantCounts(1000, 0));
    }

    [Fact]
    public void An_empty_vector_uses_the_fallback_time()
    {
        using var json = JsonDocument.Parse("""{ "status": "success", "data": { "resultType": "vector", "result": [] } }""");
        var at = new DateTimeOffset(2026, 11, 13, 0, 0, 0, TimeSpan.Zero);

        PrometheusCounterSource.Parse(json.RootElement, at).Should().Match<CounterSnapshot>(s => s.At == at && s.Series.Count == 0);
    }

    [Theory]
    [InlineData("""{ "status": "error", "errorType": "bad_data", "error": "parse error" }""")]
    [InlineData("""{ "status": "success", "data": { "resultType": "matrix", "result": [] } }""")]
    public void Rejects_errors_and_unexpected_result_types(string body)
    {
        using var json = JsonDocument.Parse(body);
        FluentActions.Invoking(() => PrometheusCounterSource.Parse(json.RootElement, DateTimeOffset.UnixEpoch))
            .Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void The_query_selects_the_configured_flag_only()
    {
        var source = new PrometheusCounterSource(new HttpClient(), new ControllerOptions { FlagKey = "new-pricing-engine" });

        source.Query.Should().Be(
            "sum by (instance, feature_flag_result_variant, outcome) (rollout_requests_total{feature_flag_key=\"new-pricing-engine\"})");
    }
}

public sealed class FlagFileStoreTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("flags-store-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private const string Json = """{ "flags": { "f": { "state": "ENABLED", "variants": { "on": true, "off": false }, "defaultVariant": "off" } } }""";

    [Fact]
    public async Task Writes_replace_the_file_and_leave_no_temporary_files()
    {
        var path = Path.Combine(_directory, "flags.json");
        await File.WriteAllTextAsync(path, Json, TestContext.Current.CancellationToken);
        var store = new FlagFileStore(path);
        var read = await store.ReadAsync(TestContext.Current.CancellationToken);

        var version = await store.WriteAsync(read.Document.WithCanaryPercent(new("f", "on", "off"), 10, "r", DateTimeOffset.UnixEpoch), read.Version, TestContext.Current.CancellationToken);

        (await store.ReadAsync(TestContext.Current.CancellationToken)).Version.Should().Be(version);
        Directory.GetFiles(_directory).Should().ContainSingle();
    }

    [Fact]
    public async Task A_concurrent_edit_makes_the_write_fail_instead_of_overwriting_it()
    {
        var path = Path.Combine(_directory, "flags.json");
        await File.WriteAllTextAsync(path, Json, TestContext.Current.CancellationToken);
        var store = new FlagFileStore(path);
        var read = await store.ReadAsync(TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(path, Json.Replace("\"off\" }", "\"on\" }", StringComparison.Ordinal), TestContext.Current.CancellationToken);

        var write = () => store.WriteAsync(read.Document, read.Version, TestContext.Current.CancellationToken);

        await write.Should().ThrowAsync<FlagFileConflictException>();
        (await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken)).Should().Contain("\"defaultVariant\": \"on\"");
    }
}

public sealed class ControllerOptionsTests
{
    private static List<ValidationResult> Validate(ControllerOptions options)
    {
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(options, new ValidationContext(options), results, validateAllProperties: true);
        results.AddRange(options.Validate(new ValidationContext(options)));
        return results;
    }

    [Fact]
    public void Valid_options_build_a_plan_and_a_policy()
    {
        var options = new ControllerOptions { Steps = [10, 50] };

        Validate(options).Should().BeEmpty();
        options.Plan().Steps.Should().Equal(10, 50);
        options.BuildPolicy().Should().BeOfType<CombinedPolicy>();
        new ControllerOptions { Policy = PolicyKind.Threshold }.BuildPolicy().Should().BeOfType<ThresholdPolicy>();
        new ControllerOptions { Policy = PolicyKind.Relative }.BuildPolicy().Should().BeOfType<RelativePolicy>();
    }

    [Theory]
    [InlineData("bad\"key")]
    [InlineData("a b")]
    [InlineData("")]
    public void Flag_keys_that_could_inject_into_PromQL_are_rejected(string key) =>
        Validate(new ControllerOptions { Steps = [10], FlagKey = key }).Should().NotBeEmpty();

    [Fact]
    public void Inconsistent_settings_are_rejected()
    {
        Validate(new ControllerOptions { Steps = [] }).Should().NotBeEmpty();
        Validate(new ControllerOptions { Steps = [50, 10] }).Should().Contain(r => r.MemberNames.Contains("Steps"));
        Validate(new ControllerOptions { Steps = [10], CanaryVariant = "x", BaselineVariant = "x" }).Should().NotBeEmpty();
        Validate(new ControllerOptions { Steps = [10], TickInterval = TimeSpan.FromSeconds(10), Window = TimeSpan.FromSeconds(15) })
            .Should().Contain(r => r.MemberNames.Contains("Window"));
    }

    [Fact]
    public void The_flag_identity_comes_from_the_options()
    {
        new ControllerOptions { FlagKey = "k", CanaryVariant = "new", BaselineVariant = "old" }.Flag.Should().Be(new CanaryFlag("k", "new", "old"));
    }
}
