namespace OrderBook.Tests;

/// <summary>
/// The tags CI splits this assembly by. One per group of classes that share a
/// leg, named as constants so a typo is a compile error rather than a leg that
/// silently matches nothing.
/// </summary>
public static class Suites
{
    public const string Domain = "domain";
    public const string Graph = "graph";

    /// <summary>
    /// MSTest's stand-in for xUnit's Assert.SkipWhen. Inconclusive is reported
    /// as skipped, so a run without Docker still shows the tests as not-run
    /// rather than passed.
    /// </summary>
    public static void SkipIfUnavailable(string? reason)
    {
        if (reason is not null) Assert.Inconclusive(reason);
    }
}
