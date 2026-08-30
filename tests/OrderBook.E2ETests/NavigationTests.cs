using Microsoft.Playwright;

namespace OrderBook.Tests;

/// <summary>
/// Loading the app and moving between its routes. Angular routing is
/// client-side, so a hard navigation exercises nginx's fallback as well as the
/// router.
/// </summary>
[Collection(E2ECollection.Name)]
public class NavigationTests(E2EFixture fixture)
{
    private async Task<IPage> FreshAsync()
    {
        Assert.SkipWhen(fixture.SkipReason is not null, fixture.SkipReason ?? string.Empty);
        return await fixture.FreshPageAsync();
    }

    [Fact]
    public async Task The_app_loads()
    {
        var page = await FreshAsync();
        await page.GotoAsync(fixture.BaseUrl);

        await Assertions.Expect(page.GetByTestId("heading")).ToHaveTextAsync("Order Book");
        await Assertions.Expect(page.GetByTestId("count")).ToHaveTextAsync("0");
    }

    [Fact]
    public async Task Routing_between_pages_works()
    {
        var page = await FreshAsync();
        await page.GotoAsync(fixture.BaseUrl);

        await page.GetByTestId("nav-audit").ClickAsync();
        await Assertions.Expect(page.GetByTestId("page")).ToHaveTextAsync("Audit");
        await Assertions.Expect(page.GetByTestId("empty")).ToBeVisibleAsync();

        await page.GetByTestId("nav-orders").ClickAsync();
        await Assertions.Expect(page.GetByTestId("page")).ToHaveTextAsync("Orders");
    }

    [Fact]
    public async Task A_deep_link_is_served_by_nginx_not_404ed()
    {
        // Angular routes are client-side. Without try_files in nginx, hitting
        // /audit directly returns a 404 from the web server and the router
        // never runs -- a failure that only a HARD navigation reveals.
        var page = await FreshAsync();

        var response = await page.GotoAsync($"{fixture.BaseUrl}/audit");

        Assert.Equal(200, response!.Status);
        await Assertions.Expect(page.GetByTestId("page")).ToHaveTextAsync("Audit");
    }
}
