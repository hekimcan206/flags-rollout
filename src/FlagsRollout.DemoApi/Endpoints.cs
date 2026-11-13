using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using OpenFeature.Model;

namespace FlagsRollout.DemoApi;

public sealed record QuoteResponse(
    string CustomerId,
    int Items,
    string Engine,
    string Variant,
    string Reason,
    long SubtotalMinor,
    long DiscountMinor,
    long TotalMinor);

public sealed record FaultSettings(double CanaryErrorRate, double BaselineErrorRate);

public static partial class Endpoints
{
    /// <summary>Label used when flagd could not be reached and the coded default was served.</summary>
    public const string DefaultVariantLabel = "(default)";

    [GeneratedRegex("^[A-Za-z0-9-]{1,64}$")]
    private static partial Regex CustomerIdPattern();

    public static IEndpointRouteBuilder MapQuoteEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/quote", GetQuote)
            .WithName("GetQuote")
            .WithSummary("Quote a cart for a customer; the pricing engine is chosen by the canary flag.")
            .Produces<QuoteResponse>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status500InternalServerError);
        return app;
    }

    public static IEndpointRouteBuilder MapAdminEndpoints(this IEndpointRouteBuilder app)
    {
        var admin = app.MapGroup("/admin").AddEndpointFilter(RequireAdminKey);
        admin.MapGet("/faults", (FaultInjector faults) => new FaultSettings(faults.CanaryErrorRate, faults.BaselineErrorRate));
        admin.MapPut("/faults", (FaultSettings settings, FaultInjector faults) =>
        {
            if (settings.CanaryErrorRate is < 0 or > 1 || settings.BaselineErrorRate is < 0 or > 1)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["errorRate"] = ["Error rates must be between 0 and 1."],
                });
            }

            faults.Set(settings.CanaryErrorRate, settings.BaselineErrorRate);
            return Results.Ok(settings);
        });
        return app;
    }

    private static async Task<IResult> GetQuote(
        string? customerId,
        int? items,
        FeatureFlagLifecycle flags,
        PricingService pricing,
        RolloutMetrics metrics,
        IOptions<CanaryOptions> canary,
        CancellationToken cancellationToken)
    {
        var cartItems = items ?? 1;
        if (customerId is null || !CustomerIdPattern().IsMatch(customerId) || cartItems is < 1 or > 100)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["customerId"] = ["Required: 1-64 letters, digits or '-'."],
                ["items"] = ["Between 1 and 100."],
            });
        }

        var flagKey = canary.Value.FlagKey;
        var context = EvaluationContext.Builder().SetTargetingKey(customerId).Build();
        var details = await flags.Client.GetBooleanDetailsAsync(flagKey, false, context, cancellationToken: cancellationToken);
        var variant = details.Variant ?? DefaultVariantLabel;

        Activity.Current?.SetTag("feature_flag.key", flagKey);
        Activity.Current?.SetTag("feature_flag.result.variant", variant);

        try
        {
            var quote = details.Value ? pricing.QuoteNew(customerId, cartItems) : pricing.QuoteLegacy(customerId, cartItems);
            metrics.Record(flagKey, variant, success: true);
            return Results.Ok(new QuoteResponse(
                customerId, cartItems, quote.Engine, variant, details.Reason ?? string.Empty,
                quote.SubtotalMinor, quote.DiscountMinor, quote.TotalMinor));
        }
        catch (InjectedFaultException ex)
        {
            metrics.Record(flagKey, variant, success: false);
            return Results.Problem(
                title: "Pricing failed",
                detail: ex.Message,
                statusCode: StatusCodes.Status500InternalServerError,
                extensions: new Dictionary<string, object?> { ["variant"] = variant });
        }
    }

    private static async ValueTask<object?> RequireAdminKey(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var expected = context.HttpContext.RequestServices.GetRequiredService<IOptions<AdminOptions>>().Value.ApiKey;
        if (string.IsNullOrEmpty(expected))
        {
            return Results.NotFound();
        }

        var supplied = context.HttpContext.Request.Headers["X-Admin-Key"].ToString();
        // Constant-time comparison: the key must not be recoverable from response timing.
        var ok = CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(supplied), Encoding.UTF8.GetBytes(expected));
        return ok ? await next(context) : Results.Unauthorized();
    }
}
