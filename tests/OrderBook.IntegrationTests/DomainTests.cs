using OrderBook;

namespace OrderBook.Tests;

/// <summary>
/// The domain, driven through the facade against a real SQL Server. No API, no
/// browser -- this is where behaviour belongs.
/// </summary>
[Collection(IntegrationCollection.Name)]
public class DomainTests(IntegrationFixture fixture)
{
    private async Task<IOrderBook> FreshAsync()
    {
        Assert.SkipWhen(fixture.SkipReason is not null, fixture.SkipReason ?? string.Empty);
        await fixture.ResetAsync();
        return await fixture.Host.GetAsync<IOrderBook>();
    }

    [Fact]
    public async Task An_order_placed_remotely_is_persisted_to_SQL()
    {
        var book = await FreshAsync();

        var reference = await book.PlaceAsync("VOD", 100);

        // "LSE" comes from OrderBookOptions, pushed into the container by the
        // fixture -- so the prefix proves which configuration served the call.
        Assert.StartsWith("LSE-", reference);

        // And the assertion the facade cannot make about itself: the row is
        // really in the database, not merely in the graph's memory.
        await using var db = fixture.Database.Connect();
        var row = db.Orders.Single();
        Assert.Equal(reference, row.Reference);
        Assert.Equal("VOD", row.Symbol);
        Assert.Equal(100, row.Quantity);
    }

    [Fact]
    public async Task A_record_round_trips_across_the_boundary()
    {
        var book = await FreshAsync();

        var reference = await book.PlaceAsync("BP", 250);
        var found = await book.FindAsync(reference);

        Assert.NotNull(found);
        Assert.Equal("BP", found!.Symbol);
        Assert.Equal(250, found.Quantity);
    }

    [Fact]
    public async Task An_exception_keeps_its_message_across_the_boundary()
    {
        var book = await FreshAsync();

        var thrown = await Record.ExceptionAsync(() => book.PlaceAsync("VOD", -1));

        Assert.NotNull(thrown);
        Assert.Contains("quantity must be positive", thrown!.Message);
    }

    [Fact]
    public async Task Two_facades_see_one_database()
    {
        // IOrderBook and IAuditLog are separate services in one graph, writing
        // to one database. The audit entry exists because the domain wrote it,
        // not because the test passed anything between them.
        var book = await FreshAsync();
        var audit = await fixture.Host.GetAsync<IAuditLog>();

        await book.PlaceAsync("RIO", 5);

        var entries = await audit.EntriesAsync();
        Assert.Single(entries);
        Assert.Contains("RIO", entries[0]);
    }
}
