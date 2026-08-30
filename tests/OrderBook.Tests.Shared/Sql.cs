using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;
using Microsoft.EntityFrameworkCore;
using OrderBook;

namespace OrderBook.Tests;

/// <summary>
/// A SQL Server container, and the two connection strings that reach it.
///
/// Shared by both test projects: the integration suite points facade
/// containers at it, and the e2e suite points the API at it. One definition,
/// so the two layers cannot disagree about the image, the password or the
/// wait strategy.
///
/// There are two because there are two vantage points, and using the wrong one
/// is the most common way this kind of fixture fails: containers reach it by
/// network alias on the Docker network, while the TEST PROCESS reaches it on a
/// port published to the host.
/// </summary>
public sealed class Sql(IContainer container)
{
    public const string Password = "Str0ng!Passw0rd";
    public const string Database = "orderbook";
    private const int Port = 1433;

    /// <summary>For containers on the same network.</summary>
    public string InternalConnection =>
        $"Server=sql,{Port};Database={Database};User Id=sa;Password={Password};TrustServerCertificate=true";

    /// <summary>For this process.</summary>
    public string ExternalConnection =>
        $"Server={container.Hostname},{container.GetMappedPublicPort(Port)};Database={Database};" +
        $"User Id=sa;Password={Password};TrustServerCertificate=true";

    public static async Task<Sql> StartAsync(INetwork network)
    {
        var container = new ContainerBuilder()
            .WithImage("mcr.microsoft.com/mssql/server:2022-latest")
            .WithNetwork(network)
            .WithNetworkAliases("sql")
            .WithEnvironment("ACCEPT_EULA", "Y")
            .WithEnvironment("MSSQL_SA_PASSWORD", Password)
            .WithPortBinding(Port, true)
            // The log line, not the port: SQL Server binds 1433 well before it
            // will accept a login, so a port check hands back a container that
            // refuses the very next connection.
            .WithWaitStrategy(Wait.ForUnixContainer()
                .UntilMessageIsLogged("SQL Server is now ready for client connections"))
            .Build();

        await container.StartAsync();
        return new Sql(container);
    }

    /// <summary>
    /// Creates the schema from the test process.
    ///
    /// Deliberately here rather than leaving it to whatever starts first. In the
    /// e2e stack the API also calls EnsureCreated, and two processes racing to
    /// create the same schema deadlock on SQL Server's metadata locks -- doing
    /// it once, before anything else starts, removes the race rather than
    /// hoping to win it.
    /// </summary>
    public async Task EnsureSchemaAsync()
    {
        var options = new DbContextOptionsBuilder<OrderBookDb>()
            .UseSqlServer(ExternalConnection).Options;

        await using var db = new OrderBookDb(options);
        await db.Database.EnsureCreatedAsync();
    }

    /// <summary>Empties both tables, so each test starts from nothing. The
    /// database outlives a facade reset, so clearing the graph is not enough.</summary>
    public async Task ResetAsync()
    {
        var options = new DbContextOptionsBuilder<OrderBookDb>()
            .UseSqlServer(ExternalConnection).Options;

        await using var db = new OrderBookDb(options);
        await db.Database.ExecuteSqlRawAsync("DELETE FROM Orders; DELETE FROM Audit;");
    }

    /// <summary>A context on the real database, for asserting what was actually
    /// persisted rather than what a page or an API said.</summary>
    public OrderBookDb Connect() =>
        new(new DbContextOptionsBuilder<OrderBookDb>().UseSqlServer(ExternalConnection).Options);

    public ValueTask DisposeAsync() => container.DisposeAsync();
}
