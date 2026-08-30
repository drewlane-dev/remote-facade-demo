using OrderBook;

namespace OrderBook.Tests;

/// <summary>
/// What the composition root gives you: substitution, per-container
/// configuration, and resolution failures that name what exists.
/// </summary>
[Trait(Suites.Name, Suites.Integration)]
[Collection(IntegrationCollection.Name)]
public class GraphTests(IntegrationFixture fixture)
{
    private async Task ReadyAsync()
    {
        Assert.SkipWhen(fixture.SkipReason is not null, fixture.SkipReason ?? string.Empty);
        await fixture.ResetAsync();
    }

    [Fact]
    public async Task A_second_startup_substitutes_a_dependency()
    {
        // The production clock is time-based, so this assertion is only
        // writable because FixedClockStartup replaced it -- in C#, in the
        // application's own code.
        await ReadyAsync();
        var book = await fixture.FixedClockHost.GetAsync<IOrderBook>();

        var reference = await book.PlaceAsync("GSK", 42);
        var found = await book.FindAsync(reference);

        Assert.Equal("2026-01-01T00:00:00.0000000Z", found!.PlacedAt);
    }

    [Fact]
    public async Task Each_container_gets_its_own_configuration()
    {
        // One startup, two containers, two venues -- pushed in as typed options
        // by the fixture. Asserted on BOTH, because checking only the second
        // would pass in a world where every container got the same value.
        await ReadyAsync();

        var primary = await fixture.Host.GetAsync<IOrderBook>();
        Assert.StartsWith("LSE-", await primary.PlaceAsync("VOD", 1));

        var secondary = await fixture.FixedClockHost.GetAsync<IOrderBook>();
        Assert.StartsWith("XETRA-", await secondary.PlaceAsync("VOD", 1));
    }

    [Fact]
    public async Task Both_containers_share_the_one_database()
    {
        // Two separate processes, one SQL Server. The count is derived from
        // rows, so the second container sees what the first wrote -- which an
        // in-memory counter never would.
        await ReadyAsync();

        var primary = await fixture.Host.GetAsync<IOrderBook>();
        var secondary = await fixture.FixedClockHost.GetAsync<IOrderBook>();

        await primary.PlaceAsync("VOD", 1);
        await secondary.PlaceAsync("BP", 2);

        Assert.Equal(2, await primary.CountAsync());
        Assert.Equal(2, await secondary.CountAsync());
    }

    [Fact]
    public async Task An_unregistered_facade_fails_at_GetAsync_naming_what_exists()
    {
        await ReadyAsync();

        var thrown = await Record.ExceptionAsync(() => fixture.Host.GetAsync<IDisposable>());

        Assert.NotNull(thrown);
        Assert.Contains(nameof(IOrderBook), thrown!.Message);
    }
}
