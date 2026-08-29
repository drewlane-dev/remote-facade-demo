using Microsoft.Playwright;

namespace OrderBook.Tests;

[Trait(Suites.Name, Suites.E2E)]
[Collection(WebUiCollection.Name)]
public class NavigationTests(WebUiFixture fixture)
{

    private async Task<IPage> FreshAsync()
    {
        Assert.SkipWhen(fixture.SkipReason is not null, fixture.SkipReason ?? string.Empty);

        // Each test starts from an empty order book. This is the payoff of
        // hosting a composition root: isolation costs a provider rebuild, not
        // a container restart -- and the UI's proxies survive it, because they
        // hold the service name rather than an instance.
        await fixture.Facade.ResetAsync();
        return await fixture.NewPageAsync();
    }

    [Fact]
    public async Task The_page_loads()
    {
        var page = await FreshAsync();
        await page.GotoAsync(fixture.BaseUrl);

        await Assertions.Expect(page.GetByTestId("heading")).ToHaveTextAsync("Orders");
        await Assertions.Expect(page.GetByTestId("count")).ToHaveTextAsync("0");
    }

    [Fact]
    public async Task Navigation_between_pages_works()
    {
        var page = await FreshAsync();
        await page.GotoAsync(fixture.BaseUrl);

        await page.GetByTestId("nav-audit").ClickAsync();
        await Assertions.Expect(page.GetByTestId("heading")).ToHaveTextAsync("Audit");
        await Assertions.Expect(page.GetByTestId("empty")).ToBeVisibleAsync();

        await page.GetByTestId("nav-orders").ClickAsync();
        await Assertions.Expect(page.GetByTestId("heading")).ToHaveTextAsync("Orders");
    }
}
