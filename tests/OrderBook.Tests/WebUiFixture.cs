using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Images;
using DotNet.Testcontainers.Networks;
using Microsoft.Playwright;
using RemoteFacadeHost.Client;

namespace OrderBook.Tests;

/// <summary>
/// Three containers and a browser, wired together.
///
///   facade  — the real OrderBook graph, hosted by remote-facade-host
///   web     — a UI that calls it, built from this repo at run time
///   browser — Playwright's server, so no browser is installed on this machine
///
/// The arrangement is the point. Playwright drives the UI, the UI calls the
/// facade, and the TEST can also reach that same facade directly — so an
/// assertion can be made on domain state rather than only on rendered HTML.
/// </summary>
public sealed class WebUiFixture : IAsyncLifetime
{
    // Must match the Microsoft.Playwright package version exactly.
    private const string PlaywrightImage = "mcr.microsoft.com/playwright:v1.62.0-noble";
    private const string PlaywrightVersion = "1.62.0";

    private INetwork _network = null!;
    private IContainer _facade = null!;
    private IContainer _web = null!;
    private IContainer _browser = null!;
    private IPlaywright _playwright = null!;

    public string? SkipReason { get; private set; }

    /// <summary>A browser connected to the containerised server.</summary>
    public IBrowser Browser { get; private set; } = null!;

    /// <summary>The UI, as the BROWSER must address it: a container alias on
    /// the shared network, not a localhost port mapped to this machine.</summary>
    public string BaseUrl => "http://web:8080";

    /// <summary>The same facade the UI talks to, for asserting on domain state.</summary>
    public RemoteHost Facade { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        // Docker availability and fixture correctness are separated
        // deliberately. Catching everything and setting SkipReason turns a bug
        // in this file into six skipped tests and a green run -- which is
        // exactly what happened the first time this fixture was written, and
        // the reason it took a log read rather than a failing assertion to
        // find a one-line mistake.
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

        // Past this point Docker works, so anything that goes wrong is ours.
        // Let it fail loudly.
        try
        {
            await StartAsync();
        }
        catch
        {
            await SafeTeardownAsync();
            throw;
        }
    }

    private async Task StartAsync()
    {

        // The SAME backend the integration layer runs, with a network attached
        // so the web app can reach it by alias. Defining it here instead would
        // let the two layers drift, and then an e2e failure would not say
        // whether the UI or the environment was at fault.
        _facade = Backend.For(typeof(DemoStartup), "LSE")
            .WithNetwork(_network)
            .WithNetworkAliases("facade")
            .Build();

        await _facade.StartAsync();

        // Built from this repo, so there is no image to publish and the UI
        // under test is always the source in the working tree.
        var webImage = new ImageFromDockerfileBuilder()
            // By .git, not by .sln: this repo has no solution file, and
            // GetSolutionDirectory() throws "Cannot find '*.sln'" rather than
            // falling back.
            .WithDockerfileDirectory(CommonDirectoryPath.GetGitDirectory(), string.Empty)
            .WithDockerfile("src/OrderBook.Web/Dockerfile")
            .WithName("orderbook-web:test")
            .WithCleanUp(false)
            .Build();

        await webImage.CreateAsync();

        _web = new ContainerBuilder()
            .WithImage(webImage)
            .WithNetwork(_network)
            .WithNetworkAliases("web")
            // The UI reaches the facade by container alias on the shared
            // network. Its own port 8080 is never published: only the browser
            // needs to reach it, and the browser is also on this network.
            .WithEnvironment("FACADE_URL", "http://facade:8080")
            // Published even though the BROWSER reaches this by network alias.
            // UntilHttpRequestIsSucceeded probes from the HOST, so it needs a
            // mapped port -- without one it waits forever on a port that was
            // never published, which is a silent hour-long hang rather than an
            // error. Publishing it also makes the UI openable from a browser
            // here when a test is being debugged.
            .WithPortBinding(8080, true)
            .WithWaitStrategy(Wait.ForUnixContainer()
                .UntilHttpRequestIsSucceeded(r => r.ForPath("/").ForPort(8080)))
            .Build();

        await _web.StartAsync();

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

        Facade = RemoteHost.At($"http://{_facade.Hostname}:{_facade.GetMappedPublicPort(8080)}");
    }

    /// <summary>A fresh page. Each test gets its own context, so cookies and
    /// storage never leak between them.</summary>
    public async Task<IPage> NewPageAsync()
    {
        var context = await Browser.NewContextAsync();
        return await context.NewPageAsync();
    }

    public async ValueTask DisposeAsync() => await SafeTeardownAsync();

    private async Task SafeTeardownAsync()
    {
        if (Facade is not null) await Facade.DisposeAsync();
        if (Browser is not null) await Browser.CloseAsync();
        _playwright?.Dispose();
        if (_browser is not null) await _browser.DisposeAsync();
        if (_web is not null) await _web.DisposeAsync();
        if (_facade is not null) await _facade.DisposeAsync();
        if (_network is not null) await _network.DeleteAsync();
    }
}

[CollectionDefinition(WebUiCollection.Name)]
public sealed class WebUiCollection : ICollectionFixture<WebUiFixture>
{
    public const string Name = "web-ui";
}
