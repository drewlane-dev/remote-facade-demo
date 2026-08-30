using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace OrderBook;

/// <summary>
/// The application's own wiring, written once and used by everything that hosts
/// this domain: the API in production, and a remote-facade-host container in
/// tests.
///
/// That sharing is the point. A test driving the facade exercises the SAME
/// registrations the API runs with, so a wiring bug cannot pass in one and fail
/// in the other.
/// </summary>
public static class Registration
{
    public static IServiceCollection AddOrderBook(this IServiceCollection services, string connectionString)
    {
        // Scoped is what a DbContext wants, but a remote call has no scope to
        // live in and the host refuses Scoped registrations by design. Singleton
        // here is deliberate and safe for this demo: one container serves calls
        // one at a time. A real service would resolve a scope per operation
        // inside a singleton facade instead.
        services.AddDbContext<OrderBookDb>(o => o.UseSqlServer(connectionString),
            contextLifetime: ServiceLifetime.Singleton,
            optionsLifetime: ServiceLifetime.Singleton);

        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<IOrderBook, OrderBookService>();
        services.AddSingleton<IAuditLog, AuditLogService>();
        return services;
    }

    /// <summary>
    /// Creates the schema if it is not there. EnsureCreated rather than
    /// migrations: this is a demo whose database is thrown away per run, and a
    /// migration history would be ceremony with nothing to migrate from.
    /// </summary>
    public static void EnsureSchema(this IServiceProvider services)
    {
        services.GetRequiredService<OrderBookDb>().Database.EnsureCreated();
    }
}
