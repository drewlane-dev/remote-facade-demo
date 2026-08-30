using Microsoft.Extensions.DependencyInjection;
using RemoteFacadeHost.Client;

namespace OrderBook;

/// <summary>
/// The composition root a remote-facade-host container runs, named in
/// LIB_REGISTRAR.
///
/// It calls the SAME AddOrderBook the API calls, so the graph a test drives is
/// the graph production runs. The only thing it adds is where configuration
/// comes from: the test pushes typed options in and this binds them, where the
/// API reads its own configuration.
/// </summary>
public static class DemoStartup
{
    public static void Configure(IServiceCollection services)
    {
        services.BindOptions<OrderBookOptions>();

        var connection = Environment.GetEnvironmentVariable("SQL_CONNECTION")
            ?? throw new InvalidOperationException(
                "SQL_CONNECTION is required. The container has no database to talk to " +
                "without it, and every call would fail later with a connection error " +
                "rather than here, at startup, naming the missing setting.");

        services.AddOrderBook(connection);
    }
}

/// <summary>The same graph with a fixed clock, so a timestamp is assertable.</summary>
public static class FixedClockStartup
{
    public static void Configure(IServiceCollection services)
    {
        DemoStartup.Configure(services);

        // Replace, not Add: the container resolves the LAST registration, and
        // Replace says the intent rather than relying on order.
        Microsoft.Extensions.DependencyInjection.Extensions.ServiceCollectionDescriptorExtensions
            .Replace(services, ServiceDescriptor.Singleton<IClock>(
                new FixedClock("2026-01-01T00:00:00.0000000Z")));
    }
}

public sealed class FixedClock(string iso) : IClock
{
    public string NowIso() => iso;
}
