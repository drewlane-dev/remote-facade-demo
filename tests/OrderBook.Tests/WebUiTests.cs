using Microsoft.Playwright;

namespace OrderBook.Tests;

/// <summary>
/// The UI, driven by a real browser, against the real domain graph.
///
/// What makes these worth having over ordinary Playwright tests is the last
/// assertion in each: the test reaches the SAME facade the UI talks to, so it
/// can check domain state rather than only what was rendered. A page can show
/// the right text for the wrong reason; the object graph cannot.
/// </summary>
[Collection(WebUiCollection.Name)]
public class WebUiTests(WebUiFixture fixture)
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
    public async Task Clicking_place_puts_a_real_order_in_the_container()
    {
        var page = await FreshAsync();
        await page.GotoAsync(fixture.BaseUrl);

        await page.GetByTestId("symbol").FillAsync("BP");
        await page.GetByTestId("quantity").FillAsync("250");
        await page.GetByTestId("place").ClickAsync();

        // The venue prefix comes from OrderBookOptions, pushed into the FACADE
        // container by the fixture -- so this string having "LSE-" in it proves
        // the click reached the real graph, configured the way the test asked.
        await Assertions.Expect(page.GetByTestId("reference")).ToContainTextAsync("LSE-");

        // The assertion the browser cannot make. Same graph, asked directly.
        var book = await fixture.Facade.GetAsync<IOrderBook>();
        Assert.Equal(1, book.Count());
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

    [Fact]
    public async Task An_order_placed_in_the_UI_shows_up_on_the_audit_page()
    {
        // Two facades, one graph. The audit entry exists because the container
        // registered a single shared AuditLog -- the UI never passes anything
        // between the two pages.
        var page = await FreshAsync();
        await page.GotoAsync(fixture.BaseUrl);

        await page.GetByTestId("symbol").FillAsync("RIO");
        await page.GetByTestId("place").ClickAsync();

        await page.GotoAsync($"{fixture.BaseUrl}/audit");
        await Assertions.Expect(page.GetByTestId("entry")).ToContainTextAsync("RIO");
    }

    [Fact]
    public async Task The_refresh_button_updates_without_a_navigation()
    {
        var page = await FreshAsync();
        await page.GotoAsync(fixture.BaseUrl);
        await Assertions.Expect(page.GetByTestId("count")).ToHaveTextAsync("0");

        // Placed OUT OF BAND, so the rendered page is stale. The count can only
        // change if the button's fetch actually ran -- a reload would also fix
        // it, which is why the URL is asserted to be unchanged.
        var book = await fixture.Facade.GetAsync<IOrderBook>();
        await book.PlaceAsync("AZN", 5);

        var before = page.Url;
        await page.GetByTestId("refresh").ClickAsync();

        await Assertions.Expect(page.GetByTestId("count")).ToHaveTextAsync("1");
        Assert.Equal(before, page.Url);
    }

    [Fact]
    public async Task A_domain_rejection_is_rendered_with_the_domain_s_own_message()
    {
        // The message is thrown by OrderBook inside the container, crosses the
        // facade protocol, is caught by the web app and rendered. Three hops,
        // and the text a user sees is the one the domain wrote.
        var page = await FreshAsync();
        await page.GotoAsync(fixture.BaseUrl);

        await page.GetByTestId("quantity").FillAsync("-1");
        await page.GetByTestId("place").ClickAsync();

        await Assertions.Expect(page.GetByTestId("error")).ToContainTextAsync("quantity must be positive");
    }
}
