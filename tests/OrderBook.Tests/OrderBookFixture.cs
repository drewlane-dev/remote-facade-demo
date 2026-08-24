using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Images;
using OrderBook;
using RemoteClassHost.Client;

namespace OrderBook.Tests;

/// <summary>
/// Starts one container hosting the demo's composition root, and hands tests a
/// <see cref="RemoteHost"/> to resolve facades from.
///
/// Read the three steps in StartAsync — they are the whole integration story.
/// </summary>
public sealed class OrderBookFixture : IAsyncLifetime
{
    private IFutureDockerImage? _image;
    private IContainer? _container;

    /// <summary>Non-null when the environment cannot run the demo.</summary>
    public string? SkipReason { get; private set; }

    /// <summary>The client. Tests resolve facades from this.</summary>
    public RemoteHost Host { get; private set; } = null!;

    /// <summary>A second host on a container whose clock is fixed.</summary>
    public RemoteHost FixedClockHost { get; private set; } = null!;

    private IContainer? _fixedClock;

    public async ValueTask InitializeAsync()
    {
        try
        {
            await StartAsync();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            SkipReason = $"demo fixture failed to start: {ex.Message}";
            await SafeTeardownAsync();
        }
    }

    private async Task StartAsync()
    {
        // STEP 1 — build the remote-class-host image from source.
        //
        // Normally this would be `.WithImage("ghcr.io/drewlane-dev/remote-class-host:1")`
        // and there would be no build step at all. The composition-root feature
        // is v1.1, which is not published yet, so the demo builds the image from
        // the sibling checkout instead. Swap this for the published tag once
        // v1.1.0 ships.
        var hostRepo = Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..", "remote-class-host"));

        if (!File.Exists(Path.Combine(hostRepo, "Dockerfile")))
        {
            throw new InvalidOperationException(
                $"expected the remote-class-host checkout at {hostRepo}. " +
                "The demo builds the image from source because composition-root " +
                "hosting is not in a published tag yet.");
        }

        _image = new ImageFromDockerfileBuilder()
            .WithDockerfileDirectory(hostRepo)
            .WithDockerfile("Dockerfile")
            .WithName("remote-class-host:demo")
            .WithCleanUp(false)
            .Build();

        await _image.CreateAsync();

        // STEP 2 — publish the application so the container can load it.
        //
        // The host loads a PUBLISH folder, not a build output: it needs the
        // dependency DLLs beside the library. The MSBuild target in this test
        // project produces it (see OrderBook.Tests.csproj).
        var plugin = Path.Combine(AppContext.BaseDirectory, "plugin");

        if (!File.Exists(Path.Combine(plugin, "OrderBook.dll")))
        {
            throw new InvalidOperationException(
                $"expected the published application at {plugin}. " +
                "The PublishApplication target in this csproj should have produced it.");
        }

        _container = await StartHostAsync(plugin, typeof(DemoStartup));
        _fixedClock = await StartHostAsync(plugin, typeof(FixedClockStartup));

        Host = RemoteHost.At($"http://localhost:{_container.GetMappedPublicPort(8080)}");
        FixedClockHost = RemoteHost.At($"http://localhost:{_fixedClock.GetMappedPublicPort(8080)}");
    }

    /// <summary>
    /// STEP 3 — start a container pointed at a composition root.
    ///
    /// Note how little configuration there is. No LIB_TYPE, no LIB_OPTIONS, no
    /// LIB_SERVICES: RemoteHostEnvironment derives LIB_ASSEMBLY and
    /// LIB_REGISTRAR from the startup TYPE, so a rename is a compile error here
    /// rather than a container that fails to start with a string mismatch.
    /// </summary>
    private static async Task<IContainer> StartHostAsync(string pluginDir, Type startup)
    {
        var builder = new ContainerBuilder()
            .WithImage("remote-class-host:demo")
            // Copied over the Docker API rather than bind-mounted, so this works
            // the same whether the test runs on the host or inside a container.
            .WithResourceMapping(new DirectoryInfo(pluginDir), "/plugin")
            .WithEnvironment("LIB_DIR", "/plugin")
            .WithPortBinding(8080, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilPortIsAvailable(8080));

        foreach (var (key, value) in RemoteHostEnvironment.For(startup))
        {
            builder = builder.WithEnvironment(key, value);
        }

        var container = builder.Build();
        await container.StartAsync();
        return container;
    }

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
