using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;

// Demo driver. Plays an operator and a crowd of customers:
//   1. starts the canary by writing the seed flag file (10%), exactly as a human would;
//   2. sends steady synthetic checkout traffic;
//   3. once the controller has advanced to FAULT_AT_PERCENT, breaks the new pricing engine (fault injection);
//   4. measures how long until the controller rolls back and customers stop reaching the broken code.
// Prints one JSON summary line and exits 0 only if the rollback happened and stuck.

var settings = Settings.FromEnvironment();
using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
var clock = Stopwatch.StartNew();
var log = (string message) => Console.Error.WriteLine($"[{clock.Elapsed.TotalSeconds,6:0.0}s] {message}");

await WaitAsync(async () => (await http.GetAsync(new Uri(settings.ApiUrl, "/health/ready"))).IsSuccessStatusCode, "demo API ready");
await WaitAsync(async () => (await http.GetAsync(new Uri(settings.ControllerUrl, "/status"))).IsSuccessStatusCode, "controller up");

await SetFaultsAsync(0, settings.BaselineErrorRate);
if (settings.SeedFlagFile is { } seed && settings.FlagFile is { } target)
{
    // Atomic replace, like the controller: flagd must never read a half-written file.
    var temp = target + ".loadgen.tmp";
    await File.WriteAllTextAsync(temp, await File.ReadAllTextAsync(seed));
    File.Move(temp, target, overwrite: true);
    log($"canary started: {target} reset from {seed}");
}

using var stop = new CancellationTokenSource();
var traffic = new Traffic(http, settings, json);
var trafficTask = traffic.RunAsync(stop.Token);

var steps = new List<int>();
async Task<ControllerStatus> StatusAsync() =>
    (await http.GetFromJsonAsync<ControllerStatus>(new Uri(settings.ControllerUrl, "/status"), json))!;

await WaitAsync(async () =>
{
    var status = await StatusAsync();
    if (status.Phase == "progressing" && (steps.Count == 0 || steps[^1] != status.Percent))
    {
        steps.Add(status.Percent);
        log($"controller: {status.Percent}% ({status.Reason})");
    }

    return status.Phase == "progressing" && status.Percent >= settings.FaultAtPercent;
}, $"canary at {settings.FaultAtPercent}%");

await SetFaultsAsync(settings.CanaryErrorRate, settings.BaselineErrorRate);
var faultAt = traffic.Now;
var faultWall = DateTimeOffset.UtcNow;
traffic.MarkFault(faultAt);
log($"fault injected: new pricing engine now fails {settings.CanaryErrorRate * 100:0}% of requests");

ControllerStatus? rolledBack = null;
await WaitAsync(async () => (rolledBack = await StatusAsync()).Phase == "rolled-back", "rollback decision");
// The decision time is the controller's own timestamp, not when this poll noticed it: every container reads the
// same Docker VM clock, so the two wall clocks are comparable to well under a millisecond.
var decisionAt = rolledBack!.LastChangeAt is { } changedAt ? faultAt + (changedAt - faultWall) : traffic.Now;
log($"controller rolled back: {rolledBack.Reason}");

// Customers are safe once no response comes from the canary for a while after the decision.
await WaitAsync(() => Task.FromResult(traffic.Now - traffic.LastCanaryResponseAt > settings.QuietPeriod), "canary traffic to stop");
await Task.Delay(settings.QuietPeriod);
await stop.CancelAsync();
await trafficTask;

var lastCanary = traffic.LastCanaryResponseAt;
var summary = new
{
    scenario = "canary-fault-rollback",
    rps = settings.RequestsPerSecond,
    canaryErrorRate = settings.CanaryErrorRate,
    stepsObserved = steps,
    faultAtPercent = steps.Count > 0 ? steps[^1] : 0,
    rolledBack = rolledBack.Phase == "rolled-back",
    faultToDecisionMs = Math.Round((decisionAt - faultAt).TotalMilliseconds),
    faultToLastCanaryResponseMs = Math.Round((lastCanary - faultAt).TotalMilliseconds),
    decisionToLastCanaryResponseMs = Math.Round((lastCanary - decisionAt).TotalMilliseconds),
    canaryRequestsAfterFault = traffic.CanaryAfterFault,
    canaryErrorsAfterFault = traffic.CanaryErrorsAfterFault,
    baselineErrorsAfterFault = traffic.BaselineErrorsAfterFault,
    canaryResponsesAfterQuiet = traffic.CanaryAfter(lastCanary + TimeSpan.FromMilliseconds(1)),
    requestsTotal = traffic.Total,
    reason = rolledBack.Reason,
};
Console.WriteLine(JsonSerializer.Serialize(summary));
return summary.rolledBack && summary.canaryResponsesAfterQuiet == 0 ? 0 : 1;

async Task SetFaultsAsync(double canary, double baseline)
{
    using var request = new HttpRequestMessage(HttpMethod.Put, new Uri(settings.ApiUrl, "/admin/faults"))
    {
        Content = JsonContent.Create(new { canaryErrorRate = canary, baselineErrorRate = baseline }),
    };
    request.Headers.Add("X-Admin-Key", settings.AdminKey);
    using var response = await http.SendAsync(request);
    response.EnsureSuccessStatusCode();
}

async Task WaitAsync(Func<Task<bool>> condition, string what)
{
    var deadline = clock.Elapsed + settings.Timeout;
    while (clock.Elapsed < deadline)
    {
        try
        {
            if (await condition())
            {
                return;
            }
        }
        catch (HttpRequestException)
        {
            // The service is still starting.
        }

        await Task.Delay(200);
    }

    Console.WriteLine(JsonSerializer.Serialize(new { scenario = "canary-fault-rollback", rolledBack = false, failure = $"timed out waiting for {what}" }));
    Environment.Exit(1);
}

internal sealed record ControllerStatus(string Phase, int Percent, string? Reason, DateTimeOffset? LastChangeAt);

internal sealed record Settings(
    Uri ApiUrl,
    Uri ControllerUrl,
    string AdminKey,
    int RequestsPerSecond,
    int Concurrency,
    int FaultAtPercent,
    double CanaryErrorRate,
    double BaselineErrorRate,
    string? FlagFile,
    string? SeedFlagFile,
    TimeSpan QuietPeriod,
    TimeSpan Timeout)
{
    public static Settings FromEnvironment()
    {
        static string Env(string name, string fallback) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : fallback;
        static double Num(string name, double fallback) => double.Parse(Env(name, fallback.ToString(CultureInfo.InvariantCulture)), CultureInfo.InvariantCulture);
        return new Settings(
            new Uri(Env("API_URL", "http://localhost:58380")),
            new Uri(Env("CONTROLLER_URL", "http://localhost:58381")),
            Env("ADMIN_KEY", "admin-dev-only"),
            (int)Num("RPS", 100),
            (int)Num("CONCURRENCY", 8),
            (int)Num("FAULT_AT_PERCENT", 25),
            Num("CANARY_ERROR_RATE", 0.30),
            Num("BASELINE_ERROR_RATE", 0.005),
            Environment.GetEnvironmentVariable("FLAG_FILE"),
            Environment.GetEnvironmentVariable("SEED_FLAG_FILE"),
            TimeSpan.FromSeconds(Num("QUIET_SECONDS", 3)),
            TimeSpan.FromSeconds(Num("TIMEOUT_SECONDS", 180)));
    }
}

/// <summary>Open-loop traffic at a fixed rate, from synthetic customers c-00000..c-09999.</summary>
internal sealed class Traffic(HttpClient http, Settings settings, JsonSerializerOptions json)
{
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Lock _gate = new();
    private readonly List<TimeSpan> _canaryResponses = [];
    private TimeSpan _faultAt = TimeSpan.MaxValue;
    private int _total;
    private int _canaryAfterFault;
    private int _canaryErrorsAfterFault;
    private int _baselineErrorsAfterFault;

    public TimeSpan Now => _clock.Elapsed;

    public int Total => Volatile.Read(ref _total);

    public int CanaryAfterFault => Volatile.Read(ref _canaryAfterFault);

    public int CanaryErrorsAfterFault => Volatile.Read(ref _canaryErrorsAfterFault);

    public int BaselineErrorsAfterFault => Volatile.Read(ref _baselineErrorsAfterFault);

    public TimeSpan LastCanaryResponseAt
    {
        get
        {
            lock (_gate)
            {
                return _canaryResponses.Count == 0 ? TimeSpan.Zero : _canaryResponses[^1];
            }
        }
    }

    public void MarkFault(TimeSpan at) => _faultAt = at;

    public int CanaryAfter(TimeSpan at)
    {
        lock (_gate)
        {
            return _canaryResponses.Count(t => t >= at);
        }
    }

    public async Task RunAsync(CancellationToken ct)
    {
        var interval = TimeSpan.FromSeconds(1.0 / settings.RequestsPerSecond);
        using var slots = new SemaphoreSlim(settings.Concurrency);
        var random = new Random(206);
        var next = _clock.Elapsed;
        var inflight = new List<Task>();
        try
        {
            while (!ct.IsCancellationRequested)
            {
                next += interval;
                var wait = next - _clock.Elapsed;
                if (wait > TimeSpan.Zero)
                {
                    await Task.Delay(wait, ct);
                }

                await slots.WaitAsync(ct);
                var customer = $"c-{random.Next(10_000):D5}";
                inflight.Add(OneAsync(customer, slots, ct));
                inflight.RemoveAll(t => t.IsCompleted);
            }
        }
        catch (OperationCanceledException)
        {
        }

        await Task.WhenAll(inflight);
    }

    private async Task OneAsync(string customer, SemaphoreSlim slots, CancellationToken ct)
    {
        try
        {
            using var response = await http.GetAsync(new Uri(settings.ApiUrl, $"/api/v1/quote?customerId={customer}&items=3"), ct);
            var body = await response.Content.ReadFromJsonAsync<JsonElement>(json, ct);
            var variant = body.TryGetProperty("variant", out var v) ? v.GetString() : null;
            var at = _clock.Elapsed;
            Interlocked.Increment(ref _total);
            var afterFault = at >= _faultAt;
            if (variant == "on")
            {
                lock (_gate)
                {
                    _canaryResponses.Add(at);
                }

                if (afterFault)
                {
                    Interlocked.Increment(ref _canaryAfterFault);
                    if (!response.IsSuccessStatusCode)
                    {
                        Interlocked.Increment(ref _canaryErrorsAfterFault);
                    }
                }
            }
            else if (afterFault && !response.IsSuccessStatusCode)
            {
                Interlocked.Increment(ref _baselineErrorsAfterFault);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            // A dropped request is neither canary nor baseline evidence; the summary counts only answered ones.
        }
        finally
        {
            slots.Release();
        }
    }
}
