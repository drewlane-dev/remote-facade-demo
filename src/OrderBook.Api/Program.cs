using Microsoft.AspNetCore.Mvc;
using OrderBook;

// The API hosts the domain DIRECTLY -- it is the application. The facade
// container hosts the same assembly so tests can drive the same graph without a
// browser; neither knows about the other.
var connection = Environment.GetEnvironmentVariable("SQL_CONNECTION")
    ?? throw new InvalidOperationException("SQL_CONNECTION is required.");

var venue = Environment.GetEnvironmentVariable("VENUE") ?? "LSE";

var builder = WebApplication.CreateBuilder(args);
builder.Services.Configure<OrderBookOptions>(o => o.Venue = venue);
builder.Services.AddOrderBook(connection);

var app = builder.Build();

// The API owns the schema. Both it and the facade point at one database, and
// two things racing to create it would deadlock on SQL Server's metadata locks.
app.Services.EnsureSchema();

// Liveness for the container wait strategy. Deliberately touches the database:
// "the process is up" is not the same as "it can serve a request", and a
// container that passes the first while failing the second turns every test
// into a confusing first-call failure.
app.MapGet("/health", async (IOrderBook book) =>
{
    await book.CountAsync();
    return Results.Ok(new { status = "ok", venue });
});

app.MapGet("/api/orders", async (IOrderBook book) =>
    Results.Ok(new { count = await book.CountAsync() }));

app.MapGet("/api/orders/{reference}", async (string reference, IOrderBook book) =>
    await book.FindAsync(reference) is { } found ? Results.Ok(found) : Results.NotFound());

app.MapPost("/api/orders", async ([FromBody] PlaceRequest request, IOrderBook book) =>
{
    try
    {
        return Results.Ok(new { reference = await book.PlaceAsync(request.Symbol, request.Quantity) });
    }
    catch (ArgumentOutOfRangeException ex)
    {
        // The domain's own message, surfaced to the browser rather than
        // becoming a 500 with nothing in it.
        return Results.BadRequest(new { error = ex.Message.Split(" (Parameter")[0] });
    }
});

app.MapGet("/api/audit", async (IAuditLog audit) => Results.Ok(await audit.EntriesAsync()));

app.Run();

public sealed record PlaceRequest(string Symbol, int Quantity);
