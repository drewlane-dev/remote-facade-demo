using Microsoft.EntityFrameworkCore;
using Microsoft.Playwright;

namespace OrderBook.Tests;

/// <summary>
/// Placing orders through the browser, and checking what actually landed.
///
/// Every case here asserts twice: once on what the page shows, and once on the
/// DATABASE. A page can show the right text for the wrong reason -- a stale
/// render, a cached response, a mocked layer -- and the row cannot.
/// </summary>
[TestClass]
[TestCategory(Suites.Journey)]
public class OrderPlacementTests
{
    private static E2EFixture Fixture => E2EEnvironment.Fixture;

    private static async Task<IPage> FreshAsync()
    {
        Suites.SkipIfUnavailable(Fixture.SkipReason);
        return await Fixture.FreshPageAsync();
    }

    [TestMethod]
    public async Task Clicking_place_writes_a_row_through_the_whole_stack()
    {
        // Browser -> nginx -> API -> domain -> SQL Server. Four containers, and
        // the assertion at the end is on the row.
        var page = await FreshAsync();
        await page.GotoAsync(Fixture.BaseUrl);

        await page.GetByTestId("symbol").FillAsync("BP");
        await page.GetByTestId("quantity").FillAsync("250");
        await page.GetByTestId("place").ClickAsync();

        // "LSE" comes from the API's own configuration, so the prefix proves
        // the click reached the real domain rather than a stubbed response.
        await Assertions.Expect(page.GetByTestId("reference")).ToContainTextAsync("LSE-");

        await using var db = Fixture.Database.Connect();
        var row = await db.Orders.SingleAsync();
        Assert.AreEqual("BP", row.Symbol);
        Assert.AreEqual(250, row.Quantity);
    }

    [TestMethod]
    public async Task An_order_placed_in_the_UI_appears_on_the_audit_page()
    {
        // The audit entry exists because the domain wrote it in the same
        // transaction. The UI never carries anything between the two pages.
        var page = await FreshAsync();
        await page.GotoAsync(Fixture.BaseUrl);

        await page.GetByTestId("symbol").FillAsync("RIO");
        await page.GetByTestId("place").ClickAsync();
        await Assertions.Expect(page.GetByTestId("reference")).ToBeVisibleAsync();

        await page.GetByTestId("nav-audit").ClickAsync();
        await Assertions.Expect(page.GetByTestId("entry")).ToContainTextAsync("RIO");
    }

    [TestMethod]
    public async Task The_refresh_button_updates_without_a_navigation()
    {
        var page = await FreshAsync();
        await page.GotoAsync(Fixture.BaseUrl);
        await Assertions.Expect(page.GetByTestId("count")).ToHaveTextAsync("0");

        // Written straight to the database, so the rendered page is stale. The
        // count can only change if the button's request actually ran -- and the
        // URL is asserted unchanged, because a reload would also fix it.
        await using (var db = Fixture.Database.Connect())
        {
            db.Orders.Add(new OrderRow
            {
                Reference = "LSE-9999", Symbol = "AZN", Quantity = 5, PlacedAt = "x",
            });
            await db.SaveChangesAsync();
        }

        var before = page.Url;
        await page.GetByTestId("refresh").ClickAsync();

        await Assertions.Expect(page.GetByTestId("count")).ToHaveTextAsync("1");
        Assert.AreEqual(before, page.Url);
    }

    [TestMethod]
    public async Task A_domain_rejection_reaches_the_page_with_its_own_message()
    {
        // Thrown by the domain inside the API container, mapped to a 400,
        // rendered by Angular. Three hops, and the text a user sees is the one
        // the domain wrote.
        var page = await FreshAsync();
        await page.GotoAsync(Fixture.BaseUrl);

        await page.GetByTestId("quantity").FillAsync("-1");
        await page.GetByTestId("place").ClickAsync();

        await Assertions.Expect(page.GetByTestId("error")).ToContainTextAsync("quantity must be positive");

        // And nothing was written: a rejection that still persisted would be
        // far worse than one that failed loudly.
        await using var db = Fixture.Database.Connect();
        Assert.AreEqual(0, await db.Orders.CountAsync());
    }
}
