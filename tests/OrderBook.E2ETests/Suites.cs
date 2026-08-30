namespace OrderBook.Tests;

/// <summary>
/// The tags CI splits this assembly by. Both classes share one browser, API,
/// web and SQL stack, and standing that up is 94-99% of the leg, so they share
/// a single tag and therefore a single leg.
/// </summary>
public static class Suites
{
    public const string Journey = "journey";

    public static void SkipIfUnavailable(string? reason)
    {
        if (reason is not null) Assert.Inconclusive(reason);
    }
}
