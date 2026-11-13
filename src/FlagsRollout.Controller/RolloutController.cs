using System.Diagnostics.Metrics;
using System.Globalization;
using System.Text.Json;
using FlagsRollout.Core.Analysis;
using FlagsRollout.Core.Flags;
using FlagsRollout.Core.Rollout;

namespace FlagsRollout.Controller;

/// <summary>What the controller knows after its last tick; served by <c>GET /status</c>.</summary>
public sealed record ControllerStatus(
    string FlagKey,
    string Phase,
    int Percent,
    string Policy,
    string? Verdict,
    string? Reason,
    VariantCounts Canary,
    VariantCounts Baseline,
    double WindowCoveredSeconds,
    bool MetricsAvailable,
    DateTimeOffset? LastTickAt,
    DateTimeOffset? LastChangeAt,
    string? LastError);

/// <summary>One flag change the controller made, as written to the audit log.</summary>
public sealed record AuditEntry(
    DateTimeOffset At,
    string FlagKey,
    string Action,
    int FromPercent,
    int ToPercent,
    string Reason,
    VariantCounts Canary,
    VariantCounts Baseline,
    string Policy);

/// <summary>
/// One control loop iteration at a time: read the flag file, read counters, judge, change the flag.
/// </summary>
/// <remarks>
/// The rollout state lives in the flag file (weights plus metadata), not in this process: a restarted controller
/// resumes from the file, and a human edit of the file is picked up as the new truth. In memory are only the counter
/// window and the breach streak, both of which are safe to lose (losing them delays a decision, it never makes one).
/// </remarks>
public sealed partial class RolloutController
{
    private readonly ControllerOptions _options;
    private readonly FlagFileStore _store;
    private readonly ICounterSource _counters;
    private readonly IHealthPolicy _policy;
    private readonly RolloutPlan _plan;
    private readonly CanaryFlag _flag;
    private readonly TimeProvider _time;
    private readonly ILogger<RolloutController> _logger;
    private readonly CounterWindow _window;
    private readonly Counter<long> _decisions;
    private readonly Counter<long> _tickFailures;

    private RolloutState? _state;
    private string? _version;
    private ControllerStatus _status;

    public RolloutController(
        ControllerOptions options,
        FlagFileStore store,
        ICounterSource counters,
        TimeProvider time,
        IMeterFactory meters,
        ILogger<RolloutController> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(meters);
        _options = options;
        _store = store;
        _counters = counters;
        _time = time;
        _logger = logger;
        _policy = options.BuildPolicy();
        _plan = options.Plan();
        _flag = options.Flag;
        _window = new CounterWindow(options.Window);

        _status = new ControllerStatus(_flag.Key, "starting", 0, _policy.Name, null, null, default, default, 0, false, null, null, null);

        var meter = meters.Create(ControllerTelemetry.MeterName);
        _decisions = meter.CreateCounter<long>("rollout.controller.decisions", "{decision}", "Flag changes made by the controller.");
        _tickFailures = meter.CreateCounter<long>("rollout.controller.tick_failures", "{tick}", "Ticks that could not complete.");
        meter.CreateObservableGauge("rollout.controller.canary_percent", () => _status.Percent, "%", "Canary share the controller last wrote or read.");
    }

    public ControllerStatus Status => _status;

    /// <summary>Not re-entrant: <see cref="RolloutWorker"/> awaits each tick before scheduling the next.</summary>
    public async Task TickAsync(CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow();

        VersionedFlagDocument file;
        try
        {
            file = await _store.ReadAsync(cancellationToken);
            SyncStateWithFile(file, now);
        }
        catch (Exception ex) when (ex is IOException or FlagDocumentException or UnauthorizedAccessException)
        {
            // Without a readable file there is nothing safe to do; keep the last state and try again next tick.
            Fail(now, "flag file unreadable: " + ex.Message, metricsAvailable: _status.MetricsAvailable);
            return;
        }

        CounterSnapshot snapshot;
        try
        {
            snapshot = await _counters.ReadAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or JsonException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            // Fail static: no metrics means no promotion and no rollback. Rolling back on a monitoring outage would turn
            // every Prometheus restart into a production change; the trade-off is written down in ADR 0004.
            Fail(now, "metrics unavailable: " + ex.Message, metricsAvailable: false);
            return;
        }

        _window.Add(snapshot);
        var stats = _window.Compute();
        var canary = stats.For(_flag.CanaryVariant);
        var baseline = stats.For(_flag.BaselineVariant);
        var verdict = _window.SnapshotCount < 2
            ? Verdict.Inconclusive("collecting the first window")
            : _policy.Evaluate(canary, baseline);

        var state = _state!;
        var decision = RolloutEngine.Decide(_plan, state, verdict, now);
        _state = decision.State;

        var changedAt = _status.LastChangeAt;
        if (decision.Action is not RolloutAction.None)
        {
            await ApplyAsync(file, state, decision, canary, baseline, now, cancellationToken);
            changedAt = now;
        }

        _status = new ControllerStatus(
            _flag.Key,
            PhaseName(_state.Phase),
            _state.Percent,
            _policy.Name,
            verdict.Kind.ToString(),
            _state.Reason ?? verdict.Reason,
            canary,
            baseline,
            stats.Covered.TotalSeconds,
            MetricsAvailable: true,
            now,
            changedAt,
            null);
    }

    private void SyncStateWithFile(VersionedFlagDocument file, DateTimeOffset now)
    {
        if (_state is not null && file.Version == _version)
        {
            return;
        }

        var percent = file.Document.GetCanaryPercent(_flag);
        var metadata = file.Document.GetRolloutMetadata(_flag);
        _state = RolloutState.Start(_plan, percent, now);
        if (metadata?.Phase == "rolled-back" && percent == 0)
        {
            _state = _state with { Reason = metadata.Reason };
        }

        if (_version is not null)
        {
            // Someone else changed the file (an operator restarting the canary after a rollback, typically). Old
            // counts describe a rollout that no longer exists, so analysis starts over.
            _window.Clear();
            var phase = PhaseName(_state.Phase);
            LogExternalChange(_flag.Key, percent, phase);
        }

        _version = file.Version;
    }

    private async Task ApplyAsync(
        VersionedFlagDocument file,
        RolloutState before,
        RolloutDecision decision,
        VariantCounts canary,
        VariantCounts baseline,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var (action, reason, document) = decision.Action switch
        {
            RolloutAction.Rollback rollback => ("rollback", rollback.Reason, file.Document.WithRollback(_flag, rollback.Reason, now)),
            RolloutAction.SetPercent set => (set.Percent >= 100 ? "promote" : "advance", set.Reason, file.Document.WithCanaryPercent(_flag, set.Percent, set.Reason, now)),
            _ => throw new InvalidOperationException($"Unknown action {decision.Action}"),
        };

        try
        {
            _version = await _store.WriteAsync(document, file.Version, cancellationToken);
        }
        catch (FlagFileConflictException) when (decision.Action is RolloutAction.Rollback rollback)
        {
            // A rollback is applied to whatever the file says now: turning the canary off is safe on any version,
            // and waiting a tick would keep serving failures.
            var fresh = await _store.ReadAsync(cancellationToken);
            _version = await _store.WriteAsync(fresh.Document.WithRollback(_flag, rollback.Reason, now), fresh.Version, cancellationToken);
        }
        catch (FlagFileConflictException ex)
        {
            // A human edited the file mid-tick: their change wins, the decision is re-derived from it next tick.
            _version = null;
            _state = before;
            LogConflict(_flag.Key, ex.Message);
            return;
        }

        _decisions.Add(1, new KeyValuePair<string, object?>("action", action));
        var entry = new AuditEntry(now, _flag.Key, action, before.Percent, decision.State.Percent, reason, canary, baseline, _policy.Name);
        LogDecision(_flag.Key, action, before.Percent, decision.State.Percent, reason);
        await AppendAuditAsync(entry, cancellationToken);
    }

    private async Task AppendAuditAsync(AuditEntry entry, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_options.AuditLog))
        {
            return;
        }

        try
        {
            var line = JsonSerializer.Serialize(entry, ControllerJson.Options) + "\n";
            await File.AppendAllTextAsync(_options.AuditLog, line, cancellationToken);
        }
        catch (IOException ex)
        {
            // The flag change already happened and is in the structured log; a full disk must not undo it.
            LogAuditFailure(_options.AuditLog, ex.Message);
        }
    }

    private void Fail(DateTimeOffset now, string error, bool metricsAvailable)
    {
        _tickFailures.Add(1);
        LogTickFailure(_flag.Key, error);
        _status = _status with { MetricsAvailable = metricsAvailable, LastTickAt = now, LastError = error };
    }

    private static string PhaseName(RolloutPhase phase) => phase switch
    {
        RolloutPhase.Progressing => "progressing",
        RolloutPhase.Promoted => "promoted",
        RolloutPhase.RolledBack => "rolled-back",
        _ => phase.ToString().ToLower(CultureInfo.InvariantCulture),
    };

    [LoggerMessage(Level = LogLevel.Warning, Message = "Flag {FlagKey}: {Action} {From}% -> {To}%: {Reason}")]
    private partial void LogDecision(string flagKey, string action, int from, int to, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Flag {FlagKey} changed outside the controller: now {Percent}% ({Phase}); analysis restarted")]
    private partial void LogExternalChange(string flagKey, int percent, string phase);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Flag {FlagKey}: write skipped, file changed concurrently: {Detail}")]
    private partial void LogConflict(string flagKey, string detail);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Flag {FlagKey}: tick skipped: {Error}")]
    private partial void LogTickFailure(string flagKey, string error);

    [LoggerMessage(Level = LogLevel.Error, Message = "Could not append to audit log {Path}: {Error}")]
    private partial void LogAuditFailure(string path, string error);
}

public static class ControllerJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web);
}

public static class ControllerTelemetry
{
    public const string MeterName = "FlagsRollout.Controller";
}
