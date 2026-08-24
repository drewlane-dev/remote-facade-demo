using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using OrderBook;
using RemoteClassHost.Client;

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
    /// Pinned to the MINOR version deliberately: composition-root hosting
    /// arrived in 1.1, and pinning to `1` would silently accept a future 1.x
    /// whose behaviour this demo has not been checked against.
    /// </summary>
    private const string HostImage = "ghcr.io/drewlane-dev/remote-class-host:1.1.0";

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
        // STEP 1 — publish the application so the container can load it.
        //
        // The host loads a PUBLISH folder, not a build output: it needs the
        // dependency DLLs beside the library. The PublishApplication target in
        // this csproj produces it.
        var plugin = Path.Combine(AppContext.BaseDirectory, "plugin");

        if (!File.Exists(Path.Combine(plugin, "OrderBook.dll")))
        {
            throw new InvalidOperationException(
                $"expected the published application at {plugin}. " +
                "The PublishApplication target in this csproj should have produced it.");
        }

        // STEP 2 — start containers pointed at a composition root.
        _container = await StartHostAsync(plugin, typeof(DemoStartup));
        _fixedClock = await StartHostAsync(plugin, typeof(FixedClockStartup));

        Host = RemoteHost.At($"http://localhost:{_container.GetMappedPublicPort(8080)}");
        FixedClockHost = RemoteHost.At($"http://localhost:{_fixedClock.GetMappedPublicPort(8080)}");
    }

    /// <summary>
    /// Note how little configuration there is. No LIB_TYPE, no LIB_OPTIONS, no
    /// LIB_SERVICES: <see cref="RemoteHostEnvironment"/> derives LIB_ASSEMBLY
    /// and LIB_REGISTRAR from the startup TYPE, so renaming it is a compile
    /// error here rather than a container that fails to start on a string
    /// mismatch.
    /// </summary>
    private static async Task<IContainer> StartHostAsync(string pluginDir, Type startup)
    {
        var builder = new ContainerBuilder()
            .WithImage(HostImage)
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

    /// <summary>
    /// A missing image is the one failure a first-time reader will hit, because
    /// v1.1.0 is not published yet. Saying so beats a raw pull error.
    /// </summary>
    private static string Explain(Exception ex)
    {
        var message = ex.ToString();

        var looksLikeMissingImage =
            message.Contains("not found", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("manifest unknown", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("pull access denied", StringComparison.OrdinalIgnoreCase);

        if (!looksLikeMissingImage)
        {
            return $"demo fixture failed to start: {ex.Message}";
        }

        return
            $"could not obtain {HostImage}.\n\n" +
            "Composition-root hosting is v1.1, which is not published yet, so this\n" +
            "tag cannot be pulled. Build it once from a remote-class-host checkout:\n\n" +
            "    ./build-host-image.sh            (or, by hand:)\n" +
            $"    docker build -t {HostImage} ../remote-class-host\n\n" +
            "Once v1.1.0 ships this step disappears and the image is simply pulled.";
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
