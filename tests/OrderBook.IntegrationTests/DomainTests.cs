using OrderBook;

namespace OrderBook.Tests;

/// <summary>
/// The domain, driven through the facade against a real SQL Server. No API, no
/// browser -- this is where behaviour belongs.
/// </summary>
[TestClass]
[TestCategory(Suites.Domain)]
public class DomainTests
{
    private static IntegrationFixture Fixture => IntegrationEnvironment.Fixture;

    private static async Task<IOrderBook> FreshAsync()
    {
        Suites.SkipIfUnavailable(Fixture.SkipReason);
        await Fixture.ResetAsync();
        return await Fixture.Host.GetAsync<IOrderBook>();
    }

    [TestMethod]
    public async Task An_order_placed_remotely_is_persisted_to_SQL()
    {
        var book = await FreshAsync();

        var reference = await book.PlaceAsync("VOD", 100);

        // "LSE" comes from OrderBookOptions, pushed into the container by the
        // fixture -- so the prefix proves which configuration served the call.
        StringAssert.StartsWith(reference, "LSE-");

        // And the assertion the facade cannot make about itself: the row is
        // really in the database, not merely in the graph's memory.
        await using var db = Fixture.Database.Connect();
        var row = db.Orders.Single();
        Assert.AreEqual(reference, row.Reference);
        Assert.AreEqual("VOD", row.Symbol);
        Assert.AreEqual(100, row.Quantity);
    }

    [TestMethod]
    public async Task A_record_round_trips_across_the_boundary()
    {
        var book = await FreshAsync();

        var reference = await book.PlaceAsync("BP", 250);
        var found = await book.FindAsync(reference);

        Assert.IsNotNull(found);
        Assert.AreEqual("BP", found!.Symbol);
        Assert.AreEqual(250, found.Quantity);
    }

    [TestMethod]
    public async Task An_exception_keeps_its_message_across_the_boundary()
    {
        var book = await FreshAsync();

        var thrown = await Try.ExceptionAsync(() => book.PlaceAsync("VOD", -1));

        Assert.IsNotNull(thrown);
        StringAssert.Contains(thrown!.Message, "quantity must be positive");
    }

    [TestMethod]
    public async Task Two_facades_see_one_database()
    {
        // IOrderBook and IAuditLog are separate services in one graph, writing
        // to one database. The audit entry exists because the domain wrote it,
        // not because the test passed anything between them.
        var book = await FreshAsync();
        var audit = await Fixture.Host.GetAsync<IAuditLog>();

        await book.PlaceAsync("RIO", 5);

        var entries = await audit.EntriesAsync();
        Assert.AreEqual(1, entries.Count);
        StringAssert.Contains(entries[0], "RIO");
    }
}
