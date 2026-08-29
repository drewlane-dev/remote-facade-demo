namespace OrderBook;

/// <summary>
/// The slice of the order book THIS application needs — declared here, in the
/// web app, and deliberately narrower than the one the tests use.
///
/// It shares no assembly with the container. RemoteHost resolves a service by
/// <c>typeof(T).FullName</c>, so what binds the two sides is the interface's
/// NAME and SHAPE, not a common reference. That is what lets a front end
/// declare exactly the operations it calls and nothing else — which is the
/// Remote Facade idea applied properly: a coarse, purpose-shaped view over a
/// graph living somewhere else.
///
/// The namespace matters as much as the type name. Rename it to
/// <c>OrderBook.Web.IOrderBook</c> and the host will report that no such
/// service is registered, listing the ones that are.
/// </summary>
public interface IOrderBook
{
    Task<string> PlaceAsync(string symbol, int quantity);
    int Count();
}

/// <summary>The audit surface, same arrangement.</summary>
public interface IAuditLog
{
    Task<IReadOnlyList<string>> EntriesAsync();
}
