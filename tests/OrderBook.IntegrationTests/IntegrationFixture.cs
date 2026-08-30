using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;
using OrderBook;
using RemoteFacadeHost.Client;

namespace OrderBook.Tests;

/// <summary>
/// The integration environment: SQL Server, and facade containers hosting the
/// real domain against it.
///
/// No API and no browser. This layer drives the domain directly, which is
/// faster, fails more precisely, and can assert things a page never shows.
/// </summary>
public sealed class IntegrationFixture
{
    private const string HostImage = "ghcr.io/drewlane-dev/remote-facade-host:3.3.2";

    private INetwork _network = null!;
    private Sql _sql = null!;
    private IContainer _facade = null!;
    private IContainer _fixedClock = null!;

    public string? SkipReason { get; private set; }

    public RemoteHost Host { get; private set; } = null!;
    public RemoteHost FixedClockHost { get; private set; } = null!;

    /// <summary>The database both containers share, for asserting on state.</summary>
    internal Sql Database => _sql;

    /// <summary>
    /// The domain published as a plugin by RemoteFacade.Client's MSBuild
    /// target, NOT this test assembly. The domain is a real library here, which
    /// is the shape a consumer actually has.
    /// </summary>
    private static string PluginDir
    {
        get
        {
            var dir = Path.Combine(AppContext.BaseDirectory, "plugin", "OrderBook.Domain");

            if (!File.Exists(Path.Combine(dir, "OrderBook.Domain.dll")))
            {
                throw new InvalidOperationException(
                    $"expected the published domain at {dir}. It is produced by the " +
                    "<RemoteFacadePlugin> item in this project, which runs after Build.");
            }

            return dir;
        }
    }

    public async Task InitializeAsync()
    {
        // Docker availability and fixture correctness are separated on purpose.
        // Catching everything and setting SkipReason turns a bug in this file
        // into a green run with every test skipped.
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

            _facade = await StartFacadeAsync(typeof(DemoStartup), "LSE");
            _fixedClock = await StartFacadeAsync(typeof(FixedClockStartup), "XETRA");

            Host = _facade.RemoteHost();
            FixedClockHost = _fixedClock.RemoteHost();
        }
        catch
        {
            await SafeTeardownAsync();
            throw;
        }
    }

    private async Task<IContainer> StartFacadeAsync(Type startup, string venue)
    {
        var container = new ContainerBuilder()
            .WithImage(HostImage)
            .WithRemoteFacade(startup, PluginDir, transport: PluginTransport.Copy)
            .WithOptions(new OrderBookOptions { Venue = venue })
            .WithNetwork(_network)
            // The container reaches SQL by alias; the connection string that
            // works from this process would not resolve inside it.
            .WithEnvironment("SQL_CONNECTION", _sql.InternalConnection)
            .Build();

        await container.StartAsync();
        return container;
    }

    /// <summary>Empties the database AND rebuilds the graphs, because the two
    /// hold different state and a test wants both clean.</summary>
    public async Task ResetAsync()
    {
        await _sql.ResetAsync();
        await Host.ResetAsync();
        await FixedClockHost.ResetAsync();
    }

    public async Task DisposeAsync() => await SafeTeardownAsync();

    private async Task SafeTeardownAsync()
    {
        if (Host is not null) await Host.DisposeAsync();
        if (FixedClockHost is not null) await FixedClockHost.DisposeAsync();
        if (_facade is not null) await _facade.DisposeAsync();
        if (_fixedClock is not null) await _fixedClock.DisposeAsync();
        if (_sql is not null) await _sql.DisposeAsync();
        if (_network is not null) await _network.DeleteAsync();
    }
}

/// <summary>
/// Starts the environment once per PROCESS and tears it down at the end.
///
/// This replaces xUnit's ICollectionFixture, and the mapping is exact rather
/// than approximate: the splitter already gives every leg its own process, so
/// "once per assembly run" and "once per leg" are the same thing. MSTest also
/// runs classes serially unless an assembly opts into [Parallelize], which is
/// what the collection's DisableParallelization was for.
/// </summary>
[TestClass]
public static class IntegrationEnvironment
{
    public static IntegrationFixture Fixture { get; private set; } = null!;

    [AssemblyInitialize]
    public static async Task StartAsync(TestContext _)
    {
        Fixture = new IntegrationFixture();
        await Fixture.InitializeAsync();
    }

    [AssemblyCleanup]
    public static async Task StopAsync() => await Fixture.DisposeAsync();
}
