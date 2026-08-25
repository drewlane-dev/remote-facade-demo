using Microsoft.Extensions.Options;

namespace OrderBook;

/// <summary>
/// The facade — the narrow surface a test drives remotely.
///
/// Everything crossing this boundary is a simple value: strings, ints, records.
/// That is deliberate, and it is the single most important design rule when
/// using remote-class-host. Arguments and return values cross BY VALUE: an
/// object with methods is serialized, rebuilt inside the container from its
/// state, and its methods run on that COPY. Mutations never come back.
///
/// Keeping the surface narrow means the interesting objects stay inside the
/// container, where the startup built them, and never have to survive a trip
/// through JSON.
/// </summary>
public interface IOrderBook
{
    Task<string> PlaceAsync(string symbol, int quantity);
    Task<OrderSummary?> FindAsync(string reference);
    int Count();
}

/// <summary>
/// A second facade, served by the SAME container. Two interfaces, one
/// composition root, addressed independently by the client.
/// </summary>
public interface IAuditLog
{
    Task<IReadOnlyList<string>> EntriesAsync();
}

/// <summary>A plain data record. Records cross the boundary cleanly.</summary>
public record OrderSummary(string Reference, string Symbol, int Quantity, string PlacedAt);

/// <summary>
/// The substitutable dependency. The real implementation is time-based, so an
/// assertion on a timestamp cannot be written against it — which is exactly why
/// a test wants to replace it.
/// </summary>
public interface IClock
{
    string NowIso();
}

public sealed class SystemClock : IClock
{
    public string NowIso() => DateTime.UtcNow.ToString("O");
}

/// <summary>Bound from configuration by the startup, not by an env var.</summary>
public sealed class OrderBookOptions
{
    public string Venue { get; set; } = "UNKNOWN";
}

/// <summary>
/// Ordinary application code. Note what is NOT here: no HTTP, no attributes, no
/// awareness that it might run in a container. It takes its dependencies through
/// the constructor like any other class.
/// </summary>
public sealed class OrderBook(IOptions<OrderBookOptions> options, IClock clock, AuditLog audit) : IOrderBook
{
    private readonly OrderBookOptions _options = options.Value;
    private readonly Dictionary<string, OrderSummary> _orders = [];
    private int _next = 1;

    public Task<string> PlaceAsync(string symbol, int quantity)
    {
        if (quantity <= 0)
        {
            // A thrown exception crosses the boundary as a thrown exception,
            // message intact. Without that, a remote failure would look like a
            // transport problem and the real cause would be lost.
            throw new ArgumentOutOfRangeException(
                nameof(quantity), quantity, "quantity must be positive");
        }

        var reference = $"{_options.Venue}-{_next++:D4}";
        _orders[reference] = new OrderSummary(reference, symbol, quantity, clock.NowIso());
        audit.Record($"placed {reference} {symbol} x{quantity}");

        return Task.FromResult(reference);
    }

    public Task<OrderSummary?> FindAsync(string reference) =>
        Task.FromResult(_orders.GetValueOrDefault(reference));

    /// <summary>
    /// Deliberately synchronous. A hosted library needs no reshaping — the host
    /// awaits what is awaitable and returns everything else directly.
    /// </summary>
    public int Count() => _orders.Count;
}

/// <summary>
/// A concrete dependency shared between the two facades, so the demo shows one
/// object graph rather than two unrelated ones.
/// </summary>
public sealed class AuditLog : IAuditLog
{
    private readonly List<string> _entries = [];

    public void Record(string entry) => _entries.Add(entry);

    public Task<IReadOnlyList<string>> EntriesAsync() =>
        Task.FromResult<IReadOnlyList<string>>(_entries.ToList());
}
