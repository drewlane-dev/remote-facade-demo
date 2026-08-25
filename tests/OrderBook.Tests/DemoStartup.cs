using Microsoft.Extensions.DependencyInjection;
// Replace() lives here, not in the main DI namespace.
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace OrderBook;

/// <summary>
/// The composition root — the whole configuration story, in C#.
///
/// This is the piece that replaces LIB_TYPE, LIB_OPTIONS and LIB_SERVICES. The
/// container is told only where to find this method (LIB_ASSEMBLY +
/// LIB_REGISTRAR); everything about how the graph is built lives here, where
/// the compiler checks it.
///
/// A factory registration handles ANY constructor shape — strings, ints,
/// records, optional parameters. That is why there is no environment variable
/// for constructor arguments: C# already says it precisely, and a JSON map of
/// parameter names would be a worse version of a constructor call.
/// </summary>
public static class DemoStartup
{
    /// <summary>
    /// Named in LIB_REGISTRAR as "OrderBook.DemoStartup.Configure".
    ///
    /// `RemoteHostEnvironment.For(typeof(DemoStartup))` derives that string, so
    /// a rename is a compile error rather than a container that fails to start.
    /// </summary>
    public static void Configure(IServiceCollection services)
    {
        // Options: no LIB_OPTIONS JSON, just an object.
        services.AddSingleton<IOptions<OrderBookOptions>>(
            Options.Create(new OrderBookOptions { Venue = "LSE" }));

        // The production clock. A test that wants a predictable timestamp edits
        // THIS line — see FixedClockStartup below for the substituted variant.
        services.AddSingleton<IClock, SystemClock>();

        // A concrete type shared by both facades. Registered once, so both get
        // the same instance and the audit log actually reflects the orders.
        services.AddSingleton<AuditLog>();

        // The two surfaces the client can ask for by name.
        services.AddSingleton<IOrderBook, OrderBook>();
        services.AddSingleton<IAuditLog>(sp => sp.GetRequiredService<AuditLog>());
    }
}

/// <summary>
/// A second composition root, identical except that the clock is fixed.
///
/// This is how a dependency is substituted under the new model: you write
/// another startup, in C#, and point the container at it. No JSON map of
/// interface names to implementation names — and because it is ordinary code,
/// it can compute values, share instances, and do anything a constructor can.
/// </summary>
public static class FixedClockStartup
{
    public static void Configure(IServiceCollection services)
    {
        DemoStartup.Configure(services);

        // Replace, not Add: the container resolves the LAST registration, and
        // Replace says the intent unambiguously rather than relying on order.
        services.Replace(ServiceDescriptor.Singleton<IClock>(new FixedClock("2026-01-01T00:00:00.0000000Z")));
    }
}

/// <summary>
/// A fake living in the application assembly. It could equally be a Moq mock
/// served back to the test process over LIB_CALLBACKS — that mechanism still
/// exists and is the right choice when you need Verify(). A fake is simpler and
/// has no network in the path, so it is the better default.
/// </summary>
public sealed class FixedClock(string iso) : IClock
{
    public string NowIso() => iso;
}
