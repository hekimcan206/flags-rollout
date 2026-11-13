namespace FlagsRollout.DemoApi;

/// <summary>A synthetic quote: the numbers are derived from the customer id so every engine is deterministic.</summary>
public sealed record Quote(string CustomerId, int Items, long SubtotalMinor, long DiscountMinor, long TotalMinor, string Engine);

/// <summary>Raised by the fault injector; stands in for whatever a real new code path would break on.</summary>
public sealed class InjectedFaultException : Exception
{
    public InjectedFaultException()
        : base("Injected fault")
    {
    }

    public InjectedFaultException(string message)
        : base(message)
    {
    }

    public InjectedFaultException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Error rates applied to each code path; changed at runtime through the admin endpoint.</summary>
public sealed class FaultInjector
{
    private double _canaryErrorRate;
    private double _baselineErrorRate;

    public FaultInjector(double canaryErrorRate, double baselineErrorRate) => Set(canaryErrorRate, baselineErrorRate);

    public double CanaryErrorRate => Volatile.Read(ref _canaryErrorRate);

    public double BaselineErrorRate => Volatile.Read(ref _baselineErrorRate);

    public void Set(double canaryErrorRate, double baselineErrorRate)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(canaryErrorRate, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(canaryErrorRate, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(baselineErrorRate, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(baselineErrorRate, 1);
        Volatile.Write(ref _canaryErrorRate, canaryErrorRate);
        Volatile.Write(ref _baselineErrorRate, baselineErrorRate);
    }

    public void MaybeFail(bool canary)
    {
        var rate = canary ? CanaryErrorRate : BaselineErrorRate;
        // Random.Shared is thread-safe; fault injection is a simulation, not a security decision.
#pragma warning disable CA5394
        if (rate > 0 && Random.Shared.NextDouble() < rate)
#pragma warning restore CA5394
        {
            throw new InjectedFaultException(canary ? "new pricing engine failed (injected)" : "legacy pricing engine failed (injected)");
        }
    }
}

/// <summary>The two code paths behind the flag.</summary>
public sealed class PricingService(FaultInjector faults)
{
    public Quote QuoteLegacy(string customerId, int items)
    {
        faults.MaybeFail(canary: false);
        var subtotal = Subtotal(customerId, items);
        // Legacy rule: flat 5% off from 5 items.
        var discount = items >= 5 ? subtotal * 5 / 100 : 0;
        return new Quote(customerId, items, subtotal, discount, subtotal - discount, "legacy");
    }

    public Quote QuoteNew(string customerId, int items)
    {
        faults.MaybeFail(canary: true);
        var subtotal = Subtotal(customerId, items);
        // New rule: 2% per item from the third, capped at 10%.
        var percent = Math.Clamp((items - 2) * 2, 0, 10);
        var discount = subtotal * percent / 100;
        return new Quote(customerId, items, subtotal, discount, subtotal - discount, "new");
    }

    private static long Subtotal(string customerId, int items)
    {
        // Unit price between 10.00 and 99.99 (minor units), stable per customer.
        // FNV-1a: a stable hash that does not change between processes (string.GetHashCode does).
        var hash = 2_166_136_261u;
        foreach (var c in customerId)
        {
            hash = (hash ^ c) * 16_777_619u;
        }

        var unitPrice = 1_000 + (hash % 9_000);
        return unitPrice * items;
    }
}
