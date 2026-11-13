using System.Net;
using System.Net.Http.Json;
using FlagsRollout.Controller;
using FlagsRollout.DemoApi;

namespace FlagsRollout.IntegrationTests;

/// <summary>The demo API against real flagd, and its metrics read back from real Prometheus.</summary>
public sealed class DemoApiTests(StackFixture stack)
{
    [Fact]
    public async Task Quotes_use_the_engine_the_flag_selects_and_are_counted_exactly_in_Prometheus()
    {
        var ct = TestContext.Current.CancellationToken;
        await File.WriteAllTextAsync(stack.FlagFile, FlagFiles.Initial("api-test-flag", 50), ct);
        await using var host = new DemoApiHost(stack, "api-test-flag");
        using var client = host.CreateClient();
        var served = new Dictionary<(string Variant, bool Ok), int>();

        async Task<string> QuoteAsync(string customerId)
        {
            using var response = await client.GetAsync(new Uri($"/api/v1/quote?customerId={customerId}&items=3", UriKind.Relative), ct);
            string variant;
            if (response.IsSuccessStatusCode)
            {
                var quote = (await response.Content.ReadFromJsonAsync<QuoteResponse>(ct))!;
                quote.Engine.Should().Be(quote.Variant == "on" ? "new" : "legacy");
                variant = quote.Variant;
            }
            else
            {
                response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
                var problem = await response.Content.ReadFromJsonAsync<Dictionary<string, object>>(ct);
                variant = problem!["variant"].ToString()!;
                variant.Should().Be("on", "only the canary path has injected faults in this test");
            }

            var key = (variant, response.IsSuccessStatusCode);
            served[key] = served.GetValueOrDefault(key) + 1;
            return variant;
        }

        await StackFixture.EventuallyAsync(
            async () => await QuoteAsync("probe") is "on" or "off",
            TimeSpan.FromSeconds(20),
            "the API to evaluate api-test-flag through flagd");
        await host.SetFaultsAsync(client, canary: 0.2, baseline: 0);
        for (var i = 0; i < 400; i++)
        {
            await QuoteAsync($"c-{i}");
        }

        served.Keys.Select(k => k.Variant).Should().Contain(["on", "off"]);
        served.GetValueOrDefault(("on", false)).Should().BePositive();

        // Read back what this instance exported and compare it with what was served, request for request.
        long Served(string variant, bool? ok = null) =>
            served.Where(s => s.Key.Variant == variant && (ok is null || s.Key.Ok == ok)).Sum(s => s.Value);
        using var http = new HttpClient { BaseAddress = stack.PrometheusUrl };
        var source = new PrometheusCounterSource(http, new ControllerOptions { FlagKey = "api-test-flag" });
        await StackFixture.EventuallyAsync(
            async () =>
            {
                var snapshot = await source.ReadAsync(ct);
                var mine = snapshot.Series.Where(s => s.Key.Instance == host.InstanceId).ToList();
                long Count(string variant, bool errors) =>
                    mine.Where(s => s.Key.Variant == variant).Sum(s => errors ? s.Value.Errors : s.Value.Requests);
                return Count("on", false) == Served("on") && Count("off", false) == Served("off") &&
                       Count("on", true) == Served("on", ok: false) && Count("off", true) == 0;
            },
            TimeSpan.FromSeconds(20),
            "Prometheus to hold exactly the requests the API served");
    }

    [Fact]
    public async Task Bad_input_and_a_wrong_admin_key_are_rejected()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = new DemoApiHost(stack, "api-test-flag");
        using var client = host.CreateClient();

        (await client.GetAsync(new Uri("/api/v1/quote?customerId=bad%20id", UriKind.Relative), ct)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await client.GetAsync(new Uri("/api/v1/quote?customerId=ok&items=0", UriKind.Relative), ct)).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        using var request = new HttpRequestMessage(HttpMethod.Put, "/admin/faults") { Content = JsonContent.Create(new FaultSettings(1, 1)) };
        request.Headers.Add("X-Admin-Key", "wrong");
        (await client.SendAsync(request, ct)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        using var invalid = new HttpRequestMessage(HttpMethod.Put, "/admin/faults") { Content = JsonContent.Create(new FaultSettings(2, 0)) };
        invalid.Headers.Add("X-Admin-Key", DemoApiHost.AdminKey);
        (await client.SendAsync(invalid, ct)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Readiness_follows_the_flagd_connection()
    {
        await using var host = new DemoApiHost(stack, "api-test-flag");
        using var client = host.CreateClient();

        var ready = await client.GetAsync(new Uri("/health/ready", UriKind.Relative), TestContext.Current.CancellationToken);

        ready.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
