using FlagsRollout.Controller;
using FlagsRollout.Core.Flags;

namespace FlagsRollout.IntegrationTests;

/// <summary>The controller's rewrites, as real flagd v0.18 reads and evaluates them.</summary>
public sealed class FlagdTests(StackFixture stack) : IDisposable
{
    private static readonly CanaryFlag Flag = new("new-pricing-engine", "on", "off");
    private readonly HttpClient _http = new();

    public void Dispose() => _http.Dispose();

    private async Task ResetAsync(int percent)
    {
        await File.WriteAllTextAsync(stack.FlagFile, FlagFiles.Initial(Flag.Key, percent), TestContext.Current.CancellationToken);
        // Wait until flagd serves the reset file; a fresh write is the only way to know the old state is gone.
        await StackFixture.EventuallyAsync(
            async () => await CanaryShareAsync(200) is var share && Math.Abs(share - percent) <= 8,
            TimeSpan.FromSeconds(15),
            $"flagd to serve {percent}%");
    }

    private async Task<double> CanaryShareAsync(int customers)
    {
        var on = 0;
        for (var i = 0; i < customers; i++)
        {
            if ((await stack.ResolveAsync(_http, Flag.Key, $"c-{i:D5}", TestContext.Current.CancellationToken)).Value)
            {
                on++;
            }
        }

        return on * 100.0 / customers;
    }

    [Fact]
    public async Task Approved_rewrites_are_valid_flagd_input_and_evaluate_as_intended()
    {
        var cases = new (string File, bool? Value, string Reason)[]
        {
            ("flags.rollback.approved.json", false, "STATIC"),
            ("flags.promote.approved.json", true, "STATIC"),
            ("flags.advance-25.approved.json", null, "TARGETING_MATCH"),
        };

        foreach (var (file, value, reason) in cases)
        {
            await File.WriteAllTextAsync(stack.FlagFile, await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "golden", file), TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);

            await StackFixture.EventuallyAsync(
                async () => await stack.ResolveAsync(_http, Flag.Key, "c-00001", TestContext.Current.CancellationToken) is var r &&
                    r.Reason == reason && (value is null || (r.Value == value && r.Variant == (value.Value ? "on" : "off"))),
                TimeSpan.FromSeconds(15),
                $"{file} to be served");
        }

        // The other flag in the file survived every rewrite and still evaluates with its own targeting.
        var banner = await stack.ResolveStringAsync(_http, "checkout-banner", "c-1", "someone@example.com", TestContext.Current.CancellationToken);
        banner.Should().Be("Summer sale <20% off>");
    }

    [Fact]
    public async Task Raising_the_canary_percentage_only_adds_customers()
    {
        await ResetAsync(10);
        var store = new FlagFileStore(stack.FlagFile);
        var customers = Enumerable.Range(0, 2_000).Select(i => $"cust-{i:D5}").ToArray();

        var at10 = await OnCanaryAsync(customers);
        var file = await store.ReadAsync(TestContext.Current.CancellationToken);
        await store.WriteAsync(file.Document.WithCanaryPercent(Flag, 25, "test", DateTimeOffset.UtcNow), file.Version, TestContext.Current.CancellationToken);
        await StackFixture.EventuallyAsync(async () => await CanaryShareAsync(200) >= 18, TimeSpan.FromSeconds(15), "25% to be served");
        var at25 = await OnCanaryAsync(customers);

        at10.Should().BeSubsetOf(at25, "a customer moved onto the new engine must not be moved back when the canary grows");
        (at10.Count / 20.0).Should().BeApproximately(10, 2.5);
        (at25.Count / 20.0).Should().BeApproximately(25, 3.5);
    }

    [Fact]
    public async Task A_rollback_written_by_the_store_reaches_every_evaluation()
    {
        await ResetAsync(50);
        var store = new FlagFileStore(stack.FlagFile);
        var file = await store.ReadAsync(TestContext.Current.CancellationToken);

        await store.WriteAsync(file.Document.WithRollback(Flag, "test", DateTimeOffset.UtcNow), file.Version, TestContext.Current.CancellationToken);
        var propagation = await StackFixture.EventuallyAsync(
            async () => await CanaryShareAsync(50) == 0,
            TimeSpan.FromSeconds(15),
            "the rollback to be served");

        (await CanaryShareAsync(500)).Should().Be(0);
        TestContext.Current.SendDiagnosticMessage($"file write -> flagd serving rollback: {propagation.TotalMilliseconds:0} ms");
    }

    private async Task<HashSet<string>> OnCanaryAsync(IEnumerable<string> customers)
    {
        var on = new HashSet<string>(StringComparer.Ordinal);
        foreach (var customer in customers)
        {
            if ((await stack.ResolveAsync(_http, Flag.Key, customer, TestContext.Current.CancellationToken)).Value)
            {
                on.Add(customer);
            }
        }

        return on;
    }
}
