using OrderBook;
using RemoteFacadeHost.Client;

namespace OrderBook.Tests;

[Trait(Suites.Name, Suites.Integration)]
[Collection(OrderBookCollection.Name)]
public class GraphTests(OrderBookFixture fixture)
{
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
    /// 6. Two containers, two configurations, from one startup.
    ///
    /// This is what typed options buy that a literal in Configure() cannot: the
    /// value varies per container, so one startup serves both. The fixture
    /// writes OrderBookOptions objects; the startup binds them with
    /// BindOptions&lt;T&gt;() and never knows a test chose them.
    ///
    /// The assertion is deliberately on BOTH hosts. Checking only the second
    /// would pass if every container somehow got XETRA, which would mean the
    /// options were not per-container at all.
    /// </summary>
    [Fact]
    public async Task Each_container_gets_its_own_configuration()
    {
        Assert.SkipWhen(fixture.SkipReason is not null, fixture.SkipReason ?? string.Empty);
        await fixture.Host.ResetAsync();
        await fixture.FixedClockHost.ResetAsync();

        var primary = await fixture.Host.GetAsync<IOrderBook>();
        var secondary = await fixture.FixedClockHost.GetAsync<IOrderBook>();

        Assert.StartsWith("LSE-", await primary.PlaceAsync("VOD", 1));
        Assert.StartsWith("XETRA-", await secondary.PlaceAsync("VOD", 1));
    }

    /// <summary>
    /// 8. ResetAsync rebuilds the whole graph — no new container.
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
    /// 9. Asking for something the startup did not register fails at GetAsync,
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
