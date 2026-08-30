using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace OrderBook;

/// <summary>
/// Ordinary application code. No HTTP, no attributes, nothing that knows it can
/// be hosted remotely or driven by a browser -- it just takes its dependencies
/// and talks to a database.
/// </summary>
public sealed class OrderBookService(
    IOptions<OrderBookOptions> options, IClock clock, OrderBookDb db) : IOrderBook
{
    private readonly OrderBookOptions _options = options.Value;

    public async Task<string> PlaceAsync(string symbol, int quantity)
    {
        if (quantity <= 0)
        {
            // Thrown inside whichever container is hosting this. It crosses the
            // facade protocol as a thrown exception with its message intact,
            // and the API renders it -- three hops, one message.
            throw new ArgumentOutOfRangeException(
                nameof(quantity), quantity, "quantity must be positive");
        }

        // Derived from the row count rather than a static counter: the state
        // lives in SQL, so two containers serving the same database agree on
        // what has been placed. An in-memory counter would not.
        var next = await db.Orders.CountAsync() + 1;
        var reference = $"{_options.Venue}-{next:D4}";

        db.Orders.Add(new OrderRow
        {
            Reference = reference,
            Symbol = symbol,
            Quantity = quantity,
            PlacedAt = clock.NowIso(),
        });
        db.Audit.Add(new AuditRow { Entry = $"placed {reference} {symbol} x{quantity}" });

        await db.SaveChangesAsync();
        return reference;
    }

    public async Task<OrderSummary?> FindAsync(string reference)
    {
        var row = await db.Orders.AsNoTracking().FirstOrDefaultAsync(o => o.Reference == reference);
        return row is null ? null : new OrderSummary(row.Reference, row.Symbol, row.Quantity, row.PlacedAt);
    }

    public Task<int> CountAsync() => db.Orders.CountAsync();
}

public sealed class AuditLogService(OrderBookDb db) : IAuditLog
{
    public async Task<IReadOnlyList<string>> EntriesAsync() =>
        await db.Audit.AsNoTracking().OrderBy(a => a.Id).Select(a => a.Entry).ToListAsync();
}
