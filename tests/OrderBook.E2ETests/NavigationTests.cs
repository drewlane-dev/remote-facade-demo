using Microsoft.Playwright;

namespace OrderBook.Tests;

/// <summary>
/// Loading the app and moving between its routes. Angular routing is
/// client-side, so a hard navigation exercises nginx's fallback as well as the
/// router.
/// </summary>
[TestClass]
[TestCategory(Suites.Journey)]
public class NavigationTests
{
    private static E2EFixture Fixture => E2EEnvironment.Fixture;

    private static async Task<IPage> FreshAsync()
    {
        Suites.SkipIfUnavailable(Fixture.SkipReason);
        return await Fixture.FreshPageAsync();
    }

    [TestMethod]
    public async Task The_app_loads()
    {
        var page = await FreshAsync();
        await page.GotoAsync(Fixture.BaseUrl);

        await Assertions.Expect(page.GetByTestId("heading")).ToHaveTextAsync("Order Book");
        await Assertions.Expect(page.GetByTestId("count")).ToHaveTextAsync("0");
    }

    [TestMethod]
    public async Task Routing_between_pages_works()
    {
        var page = await FreshAsync();
        await page.GotoAsync(Fixture.BaseUrl);

        await page.GetByTestId("nav-audit").ClickAsync();
        await Assertions.Expect(page.GetByTestId("page")).ToHaveTextAsync("Audit");
        await Assertions.Expect(page.GetByTestId("empty")).ToBeVisibleAsync();

        await page.GetByTestId("nav-orders").ClickAsync();
        await Assertions.Expect(page.GetByTestId("page")).ToHaveTextAsync("Orders");
    }

    [TestMethod]
    public async Task A_deep_link_is_served_by_nginx_not_404ed()
    {
        // Angular routes are client-side. Without try_files in nginx, hitting
        // /audit directly returns a 404 from the web server and the router
        // never runs -- a failure that only a HARD navigation reveals.
        var page = await FreshAsync();

        var response = await page.GotoAsync($"{Fixture.BaseUrl}/audit");

        Assert.AreEqual(200, response!.Status);
        await Assertions.Expect(page.GetByTestId("page")).ToHaveTextAsync("Audit");
    }
}
