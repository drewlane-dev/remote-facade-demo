using System.Net;
using OrderBook;
using RemoteFacadeHost.Client;

// The backend is a remote-facade-host container. FACADE_URL points at it.
var facadeUrl = Environment.GetEnvironmentVariable("FACADE_URL")
    ?? throw new InvalidOperationException("FACADE_URL is required.");

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

await using var host = RemoteHost.At(facadeUrl);

// Resolved once at startup, deliberately. A proxy holds the service NAME, not
// an instance, so it stays valid across a DELETE /instance on the host -- which
// is what lets a test reset domain state between cases without restarting this
// app.
var book = await host.GetAsync<IOrderBook>();
var audit = await host.GetAsync<IAuditLog>();

static string Page(string title, string body) => $"""
    <!doctype html>
    <html><head><title>{title}</title></head>
    <body>
      <h1 data-testid="heading">{title}</h1>
      <nav>
        <a href="/" data-testid="nav-orders">Orders</a> |
        <a href="/audit" data-testid="nav-audit">Audit</a>
      </nav>
      {body}
    </body></html>
    """;

// Kept out of the interpolated literal: braces inside one have to be doubled,
// which makes JavaScript unreadable and was a compile error twice over.
const string RefreshScript = """
    <button data-testid="refresh" onclick="
      fetch('/api/count').then(r => r.text()).then(t => {
        document.querySelector('[data-testid=count]').textContent = t;
      })">Refresh count</button>
    """;

app.MapGet("/", () => Results.Content(Page("Orders", $"""
    <form method="post" action="/place">
      <input name="symbol" data-testid="symbol" value="VOD" />
      <input name="quantity" data-testid="quantity" value="100" />
      <button type="submit" data-testid="place">Place order</button>
    </form>
    <p>Orders placed: <span data-testid="count">{book.Count()}</span></p>
    """ + RefreshScript), "text/html"));

app.MapPost("/place", async (HttpRequest request) =>
{
    var form = await request.ReadFormAsync();
    var symbol = form["symbol"].ToString();

    if (!int.TryParse(form["quantity"], out var quantity))
    {
        return Results.Content(Page("Orders", """<p data-testid="error">quantity must be a number</p>"""), "text/html");
    }

    try
    {
        var reference = await book.PlaceAsync(symbol, quantity);
        return Results.Content(Page("Placed", $"""<p data-testid="reference">{WebUtility.HtmlEncode(reference)}</p>"""), "text/html");
    }
    catch (Exception ex)
    {
        // The domain's own message, thrown inside the container, rendered here.
        // Proving that survives three hops is worth a test of its own.
        return Results.Content(Page("Rejected", $"""<p data-testid="error">{WebUtility.HtmlEncode(ex.Message)}</p>"""), "text/html");
    }
});

app.MapGet("/audit", async () =>
{
    var entries = await audit.EntriesAsync();
    var items = entries.Count == 0
        ? """<li data-testid="empty">nothing yet</li>"""
        : string.Join("", entries.Select(e => $"""<li data-testid="entry">{WebUtility.HtmlEncode(e)}</li>"""));

    return Results.Content(Page("Audit", $"<ul>{items}</ul>"), "text/html");
});

app.MapGet("/api/count", () => Results.Text(book.Count().ToString()));

app.Run();
