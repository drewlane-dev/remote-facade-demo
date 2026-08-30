using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Images;
using DotNet.Testcontainers.Networks;
using Microsoft.Playwright;

namespace OrderBook.Tests;

/// <summary>
/// The full stack: SQL Server, the API hosting the domain, an Angular app in
/// front of it, and a browser to drive it.
///
///   sql      SQL Server 2022
///   api      ASP.NET, hosting the SAME domain the facade hosts
///   web      nginx serving the Angular build, proxying /api to the API
///   browser  Playwright's server, so no browser is installed on this machine
///
/// The browser only ever talks to `web`, so every request is same-origin and
/// the demo needs no CORS anywhere. And because the test can open the database
/// directly, an assertion can be made on what was PERSISTED rather than on what
/// a page rendered.
/// </summary>
public sealed class E2EFixture
{
    // Must match the Microsoft.Playwright package version: the wire protocol is
    // not stable across versions and a mismatch fails at ConnectAsync.
    private const string PlaywrightImage = "mcr.microsoft.com/playwright:v1.62.0-noble";
    private const string PlaywrightVersion = "1.62.0";

    private INetwork _network = null!;
    private Sql _sql = null!;
    private IContainer _api = null!;
    private IContainer _web = null!;
    private IContainer _browser = null!;
    private IPlaywright _playwright = null!;

    public string? SkipReason { get; private set; }

    public IBrowser Browser { get; private set; } = null!;

    /// <summary>As the BROWSER must address it: an alias on the shared network,
    /// not a port mapped to this machine.</summary>
    public string BaseUrl => "http://web:8080";

    public Sql Database => _sql;

    public async Task InitializeAsync()
    {
        try
        {
            _network = new NetworkBuilder().Build();
            await _network.CreateAsync();
        }
        catch (Exception ex)
        {
            SkipReason = $"Docker is not available: {ex.Message}";
            return;
        }

        try
        {
            _sql = await Sql.StartAsync(_network);
            await _sql.EnsureSchemaAsync();

            _api = await StartAsync(await ImageAsync("src/OrderBook.Api/Dockerfile", "orderbook-api:test", "E2E_API_IMAGE"),
                "api", b => b
                    .WithEnvironment("SQL_CONNECTION", _sql.InternalConnection)
                    .WithEnvironment("VENUE", "LSE")
                    // /health touches the database, so passing it means the API
                    // can actually serve rather than merely having started.
                    .WithWaitStrategy(Wait.ForUnixContainer()
                        .UntilHttpRequestIsSucceeded(r => r.ForPath("/health").ForPort(8080))));

            _web = await StartAsync(await ImageAsync("src/OrderBook.Ui/Dockerfile", "orderbook-ui:test", "E2E_WEB_IMAGE"),
                "web", b => b.WithWaitStrategy(Wait.ForUnixContainer()
                    .UntilHttpRequestIsSucceeded(r => r.ForPath("/").ForPort(8080))));

            _browser = new ContainerBuilder()
                .WithImage(PlaywrightImage)
                .WithNetwork(_network)
                .WithCommand("npx", "-y", $"playwright@{PlaywrightVersion}", "run-server",
                    "--port", "3000", "--host", "0.0.0.0")
                .WithPortBinding(3000, true)
                .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("Listening on ws://"))
                .Build();
            await _browser.StartAsync();

            _playwright = await Playwright.CreateAsync();
            Browser = await _playwright.Chromium.ConnectAsync(
                $"ws://{_browser.Hostname}:{_browser.GetMappedPublicPort(3000)}/");
        }
        catch
        {
            await SafeTeardownAsync();
            throw;
        }
    }

    /// <summary>
    /// The image to run: the one CI built and pushed, or a local build.
    ///
    /// In CI the reference arrives as a digest, so every leg runs the same
    /// bytes and those bytes are the artifact the run produced. Building per
    /// leg instead would test something assembled on the agent that no leg can
    /// prove matches any other, and that nothing downstream will ever ship.
    /// </summary>
    private static async Task<string> ImageAsync(string dockerfile, string name, string variable)
    {
        var pinned = Images.Pinned(variable);
        if (pinned is not null) return pinned;

        var image = new ImageFromDockerfileBuilder()
            // By .git, not .sln: this repo has no solution file, and
            // GetSolutionDirectory() throws rather than falling back.
            .WithDockerfileDirectory(CommonDirectoryPath.GetGitDirectory(), string.Empty)
            .WithDockerfile(dockerfile)
            .WithName(name)
            .WithCleanUp(false)
            .Build();

        await image.CreateAsync();
        return image.FullName;
    }

    private async Task<IContainer> StartAsync(string image, string alias, Func<ContainerBuilder, ContainerBuilder> extra)
    {
        var builder = new ContainerBuilder()
            .WithImage(image)
            .WithNetwork(_network)
            .WithNetworkAliases(alias)
            // Published even though siblings reach it by alias: the HTTP wait
            // strategy probes from the HOST, so without a mapped port it waits
            // forever on a port that was never published.
            .WithPortBinding(8080, true);

        var container = extra(builder).Build();
        await container.StartAsync();
        return container;
    }

    /// <summary>A fresh page and an empty database. Each test gets its own
    /// browser context, so cookies and storage never leak between them.</summary>
    public async Task<IPage> FreshPageAsync()
    {
        await _sql.ResetAsync();
        var context = await Browser.NewContextAsync();
        return await context.NewPageAsync();
    }

    public async Task DisposeAsync() => await SafeTeardownAsync();

    private async Task SafeTeardownAsync()
    {
        if (Browser is not null) await Browser.CloseAsync();
        _playwright?.Dispose();
        if (_browser is not null) await _browser.DisposeAsync();
        if (_web is not null) await _web.DisposeAsync();
        if (_api is not null) await _api.DisposeAsync();
        if (_sql is not null) await _sql.DisposeAsync();
        if (_network is not null) await _network.DeleteAsync();
    }
}

/// <summary>
/// Starts the stack once per PROCESS and tears it down at the end, replacing
/// xUnit's ICollectionFixture. Every leg is its own process, so "once per
/// assembly run" and "once per leg" are the same thing here.
/// </summary>
[TestClass]
public static class E2EEnvironment
{
    public static E2EFixture Fixture { get; private set; } = null!;

    [AssemblyInitialize]
    public static async Task StartAsync(TestContext _)
    {
        Fixture = new E2EFixture();
        await Fixture.InitializeAsync();
    }

    [AssemblyCleanup]
    public static async Task StopAsync() => await Fixture.DisposeAsync();
}
