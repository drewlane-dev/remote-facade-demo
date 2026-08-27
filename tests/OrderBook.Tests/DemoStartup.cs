using Microsoft.Extensions.DependencyInjection;
// Replace() lives here, not in the main DI namespace.
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace OrderBook;

/// <summary>
/// The composition root — the whole configuration story, in C#.
///
/// The container is told only where to find this method (LIB_ASSEMBLY +
/// LIB_REGISTRAR); everything about how the graph is built lives here, where
/// the compiler checks it. Since v3 this is the ONLY way to host: there is no
/// single-class mode and no JSON options blob.
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
        // Options come FROM THE TEST, not from a literal here. The fixture
        // writes them with WithOptions(new OrderBookOptions { ... }) and this
        // binds them back; the type is the only shared symbol, so renaming a
        // property breaks both ends at compile time.
        //
        // BindOptions rather than a hardcoded Options.Create, because a value
        // baked in here cannot vary per container -- and two containers with
        // DIFFERENT configuration is the thing worth demonstrating.
        //
        // A missing section is a startup failure, not a silent default: pass
        // optional: true if you would rather accept OrderBookOptions' own.
        services.BindOptions<OrderBookOptions>();

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
/// A fake living in the application assembly, which is how a dependency is
/// substituted: write another startup and point a container at it.
///
/// (v2 could also proxy an interface back to the test process so a Moq mock
/// could serve it. That was removed in v3 and preserved on the host repo's
/// `callbacks` branch. A fake has no network in the path and was always the
/// better default anyway.)
/// </summary>
public sealed class FixedClock(string iso) : IClock
{
    public string NowIso() => iso;
}
