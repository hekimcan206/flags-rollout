using System.Diagnostics.Metrics;
using System.Text.Json;
using FlagsRollout.Controller;
using FlagsRollout.Core.Analysis;
using FlagsRollout.Core.Flags;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace FlagsRollout.UnitTests;

/// <summary>The control loop against a real file and a scripted counter source, with a fake clock.</summary>
public sealed class RolloutControllerTests : IDisposable
{
    private static readonly CanaryFlag Flag = new("new-pricing-engine", "on", "off");

    private readonly string _directory = Directory.CreateTempSubdirectory("flags-rollout-").FullName;
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 13, 9, 0, 0, TimeSpan.Zero));
    private readonly ScriptedCounters _counters = new();
    private readonly string _file;
    private readonly ControllerOptions _options;
    private readonly RolloutController _controller;

    public RolloutControllerTests()
    {
        _file = Path.Combine(_directory, "flags.json");
        File.WriteAllText(_file, FlagFile(10));
        _options = new ControllerOptions
        {
            FlagFile = _file,
            TickInterval = TimeSpan.FromSeconds(5),
            Window = TimeSpan.FromSeconds(30),
            Steps = [10, 25, 50],
            BakeTime = TimeSpan.FromSeconds(20),
            BreachesToRollback = 2,
            Policy = PolicyKind.Combined,
            MinRequests = 100,
            AuditLog = Path.Combine(_directory, "audit.jsonl"),
        };
        _controller = new RolloutController(_options, new FlagFileStore(_file), _counters, _time, new TestMeterFactory(), NullLogger<RolloutController>.Instance);
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private static string FlagFile(int percent) => FlagDocument
        .Parse("""{ "flags": { "new-pricing-engine": { "state": "ENABLED", "variants": { "on": true, "off": false }, "defaultVariant": "off" } } }""")
        .WithCanaryPercent(Flag, percent, "seed", DateTimeOffset.UnixEpoch)
        .ToJson();

    private int PercentInFile() => FlagDocument.Parse(File.ReadAllText(_file)).GetCanaryPercent(Flag);

    /// <summary>Serves traffic for one tick at the given error rates and advances the clock.</summary>
    private async Task TickAsync(double canaryErrorRate, double baselineErrorRate = 0.01, int canaryRequests = 100, int baselineRequests = 300)
    {
        _counters.Serve("on", canaryRequests, (int)Math.Round(canaryRequests * canaryErrorRate));
        _counters.Serve("off", baselineRequests, (int)Math.Round(baselineRequests * baselineErrorRate));
        _counters.At = _time.GetUtcNow();
        await _controller.TickAsync(TestContext.Current.CancellationToken);
        _time.Advance(_options.TickInterval);
    }

    [Fact]
    public async Task A_healthy_canary_walks_the_plan_and_is_promoted()
    {
        for (var i = 0; i < 30 && _controller.Status.Phase != "promoted"; i++)
        {
            await TickAsync(canaryErrorRate: 0.01);
        }

        _controller.Status.Phase.Should().Be("promoted");
        PercentInFile().Should().Be(100);
        var audit = File.ReadAllLines(_options.AuditLog).Select(l => JsonSerializer.Deserialize<AuditEntry>(l, ControllerJson.Options)!).ToList();
        audit.Select(a => (a.Action, a.FromPercent, a.ToPercent)).Should().Equal(
            ("advance", 10, 25), ("advance", 25, 50), ("promote", 50, 100));
    }

    [Fact]
    public async Task A_failing_canary_is_rolled_back_after_two_consecutive_breaches()
    {
        await TickAsync(0.01);
        await TickAsync(0.01);
        await TickAsync(0.30);
        PercentInFile().Should().Be(10, "one breach is not enough");

        await TickAsync(0.30);

        PercentInFile().Should().Be(0);
        _controller.Status.Phase.Should().Be("rolled-back");
        var metadata = FlagDocument.Parse(File.ReadAllText(_file)).GetRolloutMetadata(Flag)!;
        metadata.Phase.Should().Be("rolled-back");
        metadata.Reason.Should().Contain("2 unhealthy checks in a row at 10%");
        File.ReadAllLines(_options.AuditLog).Should().ContainSingle().Which.Should().Contain("\"action\":\"rollback\"");
    }

    [Fact]
    public async Task A_shared_incident_does_not_roll_back_the_canary()
    {
        for (var i = 0; i < 4; i++)
        {
            await TickAsync(canaryErrorRate: 0.20, baselineErrorRate: 0.20);
        }

        PercentInFile().Should().BeGreaterThan(0);
        _controller.Status.Phase.Should().Be("progressing");
    }

    [Fact]
    public async Task Without_metrics_the_controller_holds_and_reports_it()
    {
        await TickAsync(0.01);
        _counters.Fail = true;

        for (var i = 0; i < 10; i++)
        {
            await TickAsync(0.50);
        }

        PercentInFile().Should().Be(10);
        _controller.Status.MetricsAvailable.Should().BeFalse();
        _controller.Status.LastError.Should().Contain("metrics unavailable");
    }

    [Fact]
    public async Task A_rolled_back_flag_stays_off_until_a_human_restarts_it_then_analysis_starts_fresh()
    {
        await TickAsync(0.01);
        await TickAsync(0.40);
        await TickAsync(0.40);
        PercentInFile().Should().Be(0);

        for (var i = 0; i < 10; i++)
        {
            await TickAsync(0.01);
        }

        PercentInFile().Should().Be(0, "the controller never turns a rolled-back flag back on");

        File.WriteAllText(_file, FlagFile(10));   // operator restarts the canary after a fix
        await TickAsync(0.01);
        await TickAsync(0.01);

        _controller.Status.Phase.Should().Be("progressing");
        PercentInFile().Should().Be(10);
    }

    [Fact]
    public async Task A_restarted_controller_resumes_from_the_file()
    {
        File.WriteAllText(_file, FlagFile(25));
        await TickAsync(0.01);

        _controller.Status.Percent.Should().Be(25);
        _controller.Status.Phase.Should().Be("progressing");
    }

    [Fact]
    public async Task An_unreadable_flag_file_skips_the_tick()
    {
        File.WriteAllText(_file, "{ not json");

        await TickAsync(0.5);

        _controller.Status.LastError.Should().Contain("flag file unreadable");
    }

    private sealed class ScriptedCounters : ICounterSource
    {
        private readonly Dictionary<SeriesKey, VariantCounts> _totals = [];

        public DateTimeOffset At { get; set; }

        public bool Fail { get; set; }

        public void Serve(string variant, int requests, int errors)
        {
            var key = new SeriesKey("api-1", variant);
            _totals[key] = (_totals.TryGetValue(key, out var c) ? c : default) + new VariantCounts(requests, errors);
        }

        public Task<CounterSnapshot> ReadAsync(CancellationToken cancellationToken) => Fail
            ? throw new HttpRequestException("connection refused")
            : Task.FromResult(new CounterSnapshot(At, new Dictionary<SeriesKey, VariantCounts>(_totals)));
    }
}

internal sealed class TestMeterFactory : IMeterFactory
{
    private readonly List<Meter> _meters = [];

    public Meter Create(MeterOptions options)
    {
        var meter = new Meter(options.Name, options.Version, options.Tags, scope: this);
        _meters.Add(meter);
        return meter;
    }

    public void Dispose()
    {
        foreach (var meter in _meters)
        {
            meter.Dispose();
        }
    }
}
