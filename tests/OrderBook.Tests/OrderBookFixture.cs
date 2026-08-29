using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using OrderBook;
using RemoteFacadeHost.Client;

namespace OrderBook.Tests;

/// <summary>
/// Starts containers hosting the demo's composition root, and hands tests a
/// <see cref="RemoteHost"/> to resolve facades from.
///
/// This references the host image by TAG, exactly as a production consumer
/// would. It has no idea a Dockerfile exists anywhere.
/// </summary>
public sealed class OrderBookFixture : IAsyncLifetime
{
    /// <summary>
    /// The published image and version this demo is written against.
    ///
    /// Pinned exactly, not to `3`: this demo shows behaviour that arrived in
    /// specific releases, and floating on a major would silently accept a
    /// version it has never been run against.
    /// </summary>
    // The image and plugin directory now live in Backend, so both layers
    // cannot drift apart.

    /// <summary>
    /// The venue each container is configured with, pushed in from HERE rather
    /// than baked into the startup. Two containers, two values: that is the
    /// point of typed options, and it is not expressible with a literal in
    /// Configure().
    /// </summary>
    private const string PrimaryVenue = "LSE";
    private const string SecondaryVenue = "XETRA";

    private IContainer? _container;
    private IContainer? _fixedClock;

    /// <summary>Non-null when the environment cannot run the demo.</summary>
    public string? SkipReason { get; private set; }

    /// <summary>The client. Tests resolve facades from this.</summary>
    public RemoteHost Host { get; private set; } = null!;

    /// <summary>A second host whose composition root fixes the clock.</summary>
    public RemoteHost FixedClockHost { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        try
        {
            await StartAsync();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            SkipReason = Explain(ex);
            await SafeTeardownAsync();
        }
    }

    private async Task StartAsync()
    {
        // STEP 1 — the application IS this test assembly.
        //
        // The startup and facade are defined right here in the test project, so
        // the container loads this project's own output directory. Nothing has
        // to be published anywhere first.
        //
        // Note what is NOT possible: pointing <RemoteFacadePlugin> at this same
        // project. That target runs after Build and needs Publish, which needs
        // Build — MSBuild rejects it with MSB4006, a circular dependency. The
        // plugin item is for publishing a SEPARATE library; when the code lives
        // here, use this directory directly instead.
        var plugin = AppContext.BaseDirectory;

        if (!File.Exists(Path.Combine(plugin, "OrderBook.Tests.dll")))
        {
            throw new InvalidOperationException(
                $"expected this test assembly at {plugin}, which is also the " +
                "folder the container loads.");
        }

        // STEP 2 — start containers pointed at a composition root, each with
        // its OWN configuration.
        _container = await StartHostAsync(plugin, typeof(DemoStartup), PrimaryVenue);
        _fixedClock = await StartHostAsync(plugin, typeof(FixedClockStartup), SecondaryVenue);

        // RemoteHost() resolves the MAPPED port, so there is no URL to build
        // by hand and no chance of reaching the container's own 8080 instead.
        Host = _container.RemoteHost();
        FixedClockHost = _fixedClock.RemoteHost();
    }

    /// <summary>
    /// Everything a facade container needs, in one call.
    ///
    /// WithRemoteFacade supplies the plugin transport, LIB_DIR, LIB_ASSEMBLY,
    /// LIB_REGISTRAR, a random port binding and a wait on /health. The last of
    /// those matters: the host binds its port BEFORE the service graph is
    /// built, so waiting on the port can hand back a container that is
    /// listening and cannot yet answer anything.
    ///
    /// LIB_ASSEMBLY and LIB_REGISTRAR are derived from the startup TYPE, so
    /// renaming it is a compile error here rather than a container that starts
    /// and cannot find what it was told to serve.
    /// </summary>
    private static async Task<IContainer> StartHostAsync(string pluginDir, Type startup, string venue)
    {
        // One definition, shared with the e2e layer. See Backend.
        var container = Backend.For(startup, venue).Build();
        await container.StartAsync();
        return container;
    }

    /// <summary>
    /// A pull failure is the one thing a first-time reader is likely to hit, so
    /// name the image rather than letting a raw Docker error through.
    /// </summary>
    private static string Explain(Exception ex) =>
        $"demo fixture failed to start (image {Backend.Image}): {ex.Message}";

    public async ValueTask DisposeAsync() => await SafeTeardownAsync();

    private async Task SafeTeardownAsync()
    {
        if (Host is not null) await Host.DisposeAsync();
        if (FixedClockHost is not null) await FixedClockHost.DisposeAsync();
        if (_container is not null) await _container.DisposeAsync();
        if (_fixedClock is not null) await _fixedClock.DisposeAsync();
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class OrderBookCollection : ICollectionFixture<OrderBookFixture>
{
    /// <summary>
    /// Serial because the tests share one container and reset it between them.
    /// </summary>
    public const string Name = "Order book";
}
