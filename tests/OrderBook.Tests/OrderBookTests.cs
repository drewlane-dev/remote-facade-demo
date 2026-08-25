using OrderBook;
using RemoteFacadeHost.Client;

namespace OrderBook.Tests;

/// <summary>
/// The demo, in the order worth reading it.
///
/// Every test here drives a REAL instance of OrderBook, constructed by the real
/// DemoStartup, running inside a container — not a mock, not an in-process
/// object. The only thing the test holds is the facade interface.
/// </summary>
[Collection(OrderBookCollection.Name)]
public class OrderBookTests(OrderBookFixture fixture)
{
    /// <summary>1. A remote instance is called like a local one.</summary>
    [Fact]
    public async Task Calling_a_remote_instance_looks_local()
    {
        Assert.SkipWhen(fixture.SkipReason is not null, fixture.SkipReason ?? string.Empty);
        await fixture.Host.ResetAsync();

        var book = await fixture.Host.GetAsync<IOrderBook>();

        var reference = await book.PlaceAsync("VOD", 100);

        // "LSE" comes from OrderBookOptions, set in DemoStartup — in C#, not in
        // a LIB_OPTIONS environment variable.
        Assert.StartsWith("LSE-", reference);
    }

    /// <summary>
    /// 2. A record crosses the boundary cleanly, because it is only data.
    /// </summary>
    [Fact]
    public async Task A_record_round_trips()
    {
        Assert.SkipWhen(fixture.SkipReason is not null, fixture.SkipReason ?? string.Empty);
        await fixture.Host.ResetAsync();

        var book = await fixture.Host.GetAsync<IOrderBook>();

        var reference = await book.PlaceAsync("BP", 250);
        var found = await book.FindAsync(reference);

        Assert.NotNull(found);
        Assert.Equal("BP", found!.Symbol);
        Assert.Equal(250, found.Quantity);
    }

    /// <summary>
    /// 3. A synchronous method needs no reshaping. The host awaits what is
    /// awaitable and returns everything else directly.
    /// </summary>
    [Fact]
    public async Task A_synchronous_method_works_unchanged()
    {
        Assert.SkipWhen(fixture.SkipReason is not null, fixture.SkipReason ?? string.Empty);
        await fixture.Host.ResetAsync();

        var book = await fixture.Host.GetAsync<IOrderBook>();
        await book.PlaceAsync("SHEL", 10);

        // int, not Task<int> — invoked over HTTP all the same.
        Assert.Equal(1, book.Count());
    }

    /// <summary>
    /// 4. Two facades, ONE container and ONE object graph.
    ///
    /// The audit log sees the order because DemoStartup registered a single
    /// shared AuditLog. This is the payoff of hosting a composition root rather
    /// than one class: the graph is real, so the relationships between its parts
    /// are real too.
    /// </summary>
    [Fact]
    public async Task Two_facades_share_one_object_graph()
    {
        Assert.SkipWhen(fixture.SkipReason is not null, fixture.SkipReason ?? string.Empty);
        await fixture.Host.ResetAsync();

        var book = await fixture.Host.GetAsync<IOrderBook>();
        var audit = await fixture.Host.GetAsync<IAuditLog>();

        await book.PlaceAsync("RIO", 5);

        var entries = await audit.EntriesAsync();

        Assert.Single(entries);
        Assert.Contains("RIO", entries[0]);
    }

    /// <summary>
    /// 5. Substituting a dependency means writing another startup in C#.
    ///
    /// The production clock is time-based, so this assertion is only writable
    /// because FixedClockStartup replaced it.
    /// </summary>
    [Fact]
    public async Task A_second_startup_substitutes_a_dependency()
    {
        Assert.SkipWhen(fixture.SkipReason is not null, fixture.SkipReason ?? string.Empty);
        await fixture.FixedClockHost.ResetAsync();

        var book = await fixture.FixedClockHost.GetAsync<IOrderBook>();

        var reference = await book.PlaceAsync("GSK", 42);
        var found = await book.FindAsync(reference);

        Assert.Equal("2026-01-01T00:00:00.0000000Z", found!.PlacedAt);
    }

    /// <summary>
    /// 6. An exception keeps its message across the boundary.
    ///
    /// Without this, a remote failure would look like a transport error and the
    /// real cause would be lost.
    /// </summary>
    [Fact]
    public async Task An_exception_survives_the_boundary()
    {
        Assert.SkipWhen(fixture.SkipReason is not null, fixture.SkipReason ?? string.Empty);
        await fixture.Host.ResetAsync();

        var book = await fixture.Host.GetAsync<IOrderBook>();

        var thrown = await Record.ExceptionAsync(() => book.PlaceAsync("VOD", -1));

        Assert.NotNull(thrown);
        Assert.Contains("quantity must be positive", thrown!.Message);
    }

    /// <summary>
    /// 7. ResetAsync rebuilds the whole graph — no new container.
    ///
    /// This is what makes per-test isolation cheap: a fresh object graph costs a
    /// provider rebuild rather than a container start. Note what it does NOT
    /// clear: anything outside the process, such as files on a mounted share.
    /// </summary>
    [Fact]
    public async Task Reset_gives_each_test_a_clean_graph()
    {
        Assert.SkipWhen(fixture.SkipReason is not null, fixture.SkipReason ?? string.Empty);

        var book = await fixture.Host.GetAsync<IOrderBook>();

        await fixture.Host.ResetAsync();
        await book.PlaceAsync("AZN", 1);
        Assert.Equal(1, book.Count());

        await fixture.Host.ResetAsync();

        // Same proxy, rebuilt graph. The proxy holds the service NAME, not a
        // handle to an instance, so it binds to whatever the container has now.
        Assert.Equal(0, book.Count());
    }

    /// <summary>
    /// 8. Asking for something the startup did not register fails at GetAsync,
    /// naming what IS registered — not later, at a confusing call site.
    /// </summary>
    [Fact]
    public async Task An_unregistered_facade_fails_immediately_and_says_what_exists()
    {
        Assert.SkipWhen(fixture.SkipReason is not null, fixture.SkipReason ?? string.Empty);

        var thrown = await Record.ExceptionAsync(() => fixture.Host.GetAsync<IDisposable>());

        Assert.NotNull(thrown);
        Assert.Contains("IOrderBook", thrown!.Message);
    }
}
