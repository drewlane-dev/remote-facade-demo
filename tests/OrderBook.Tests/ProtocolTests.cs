using OrderBook;
using RemoteFacadeHost.Client;

namespace OrderBook.Tests;

[Trait(Suites.Name, Suites.Integration)]
[Collection(OrderBookCollection.Name)]
public class ProtocolTests(OrderBookFixture fixture)
{
    /// <summary>1. A remote instance is called like a local one.</summary>
    [Fact]
    public async Task Calling_a_remote_instance_looks_local()
    {
        Assert.SkipWhen(fixture.SkipReason is not null, fixture.SkipReason ?? string.Empty);
        await fixture.Host.ResetAsync();

        var book = await fixture.Host.GetAsync<IOrderBook>();

        var reference = await book.PlaceAsync("VOD", 100);

        // "LSE" was pushed in BY THE FIXTURE as a typed OrderBookOptions, not
        // baked into the startup and not spelled as an environment variable.
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
    /// 7. An exception keeps its message across the boundary.
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
}
