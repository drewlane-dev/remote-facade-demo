using Microsoft.EntityFrameworkCore;

namespace OrderBook;

/// <summary>An order, as stored. A record for the wire, a row in SQL.</summary>
public sealed record OrderSummary(string Reference, string Symbol, int Quantity, string PlacedAt);

/// <summary>
/// The facade the browser's API and the tests both ask for.
///
/// Narrow on purpose: everything crossing a remote boundary is JSON, so the
/// interesting objects stay behind it.
/// </summary>
public interface IOrderBook
{
    Task<string> PlaceAsync(string symbol, int quantity);
    Task<OrderSummary?> FindAsync(string reference);
    Task<int> CountAsync();
}

/// <summary>A second facade over the same graph and the same database.</summary>
public interface IAuditLog
{
    Task<IReadOnlyList<string>> EntriesAsync();
}

public interface IClock
{
    string NowIso();
}

public sealed class SystemClock : IClock
{
    public string NowIso() => DateTimeOffset.UtcNow.ToString("O");
}

public sealed class OrderBookOptions
{
    /// <summary>Prefixes every reference, so a test can prove which container
    /// configuration actually served a call.</summary>
    public string Venue { get; set; } = "UNKNOWN";
}

public sealed class OrderRow
{
    public int Id { get; set; }
    public string Reference { get; set; } = "";
    public string Symbol { get; set; } = "";
    public int Quantity { get; set; }
    public string PlacedAt { get; set; } = "";
}

public sealed class AuditRow
{
    public int Id { get; set; }
    public string Entry { get; set; } = "";
}

public sealed class OrderBookDb(DbContextOptions<OrderBookDb> options) : DbContext(options)
{
    public DbSet<OrderRow> Orders => Set<OrderRow>();
    public DbSet<AuditRow> Audit => Set<AuditRow>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        // Unique, because the reference is what a caller holds onto. A
        // duplicate would surface as a lookup returning the wrong order rather
        // than as an error.
        b.Entity<OrderRow>().HasIndex(o => o.Reference).IsUnique();
    }
}
