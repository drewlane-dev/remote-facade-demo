# remote-class-demo

A small, runnable demonstration of hosting a **composition root** with
[`remote-facade-host`](https://github.com/drewlane-dev/remote-facade-host): your
real application code runs inside a container, and your tests drive it through a
narrow interface as if it were local.

```bash
dotnet run --project tests/OrderBook.Tests
```

Eight tests, about three seconds, two containers. Docker is the only
prerequisite.

## The two files that matter

Everything else is scaffolding. The pattern is these two.

**1. A startup — all the wiring, in C#** ([`DemoStartup.cs`](tests/OrderBook.Tests/DemoStartup.cs)):

```csharp
public static class DemoStartup
{
    public static void Configure(IServiceCollection services)
    {
        services.AddSingleton<IOptions<OrderBookOptions>>(
            Options.Create(new OrderBookOptions { Venue = "LSE" }));
        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<AuditLog>();
        services.AddSingleton<IOrderBook, OrderBook>();
        services.AddSingleton<IAuditLog>(sp => sp.GetRequiredService<AuditLog>());
    }
}
```

**2. A facade — the surface the test drives** ([`OrderBook.cs`](tests/OrderBook.Tests/OrderBook.cs)):

```csharp
public interface IOrderBook
{
    Task<string> PlaceAsync(string symbol, int quantity);
    Task<OrderSummary?> FindAsync(string reference);
    int Count();
}
```

Then the test:

```csharp
var book = await host.GetAsync<IOrderBook>();
var reference = await book.PlaceAsync("VOD", 100);   // runs in the container
```

## What the container is told

Two environment variables, both derived from the startup **type** — so renaming
it is a compile error rather than a container that fails to start:

```csharp
.WithRemoteFacade(typeof(DemoStartup), pluginDir, transport: PluginTransport.Copy)
```

That supplies `LIB_ASSEMBLY=OrderBook.dll`,
`LIB_REGISTRAR=OrderBook.DemoStartup.Configure`, the plugin transport, a random
port binding and a wait on `/health` — not on the port, which the host binds
*before* the graph is built.

Since v3 a startup is the only way to host; `LIB_TYPE` and `LIB_OPTIONS` are
gone. The startup says all of it in C#, where a factory registration handles
any constructor shape the language allows.

## Configuration comes from the test

The venue is not a literal in the startup. The fixture pushes a typed object in
and the startup binds it back:

```csharp
// fixture — one container per venue
.WithOptions(new OrderBookOptions { Venue = "LSE" })

// DemoStartup
services.BindOptions<OrderBookOptions>();
```

`OrderBookOptions` is the only shared symbol, so renaming a property breaks
both ends at compile time — and because the value is per container, one startup
serves two differently-configured instances. A section the fixture never set is
a startup failure, not a silent default.

## Browser tests, with the browser in a container too

`WebUiFixture` starts three containers and drives them with Playwright:

```
facade   the real OrderBook graph, hosted by remote-facade-host
web      a small UI that calls it, built from this repo at run time
browser  Playwright's server, so nothing is installed on your machine
```

The UI declares **its own** narrow view of the domain:

```csharp
namespace OrderBook;

public interface IOrderBook
{
    Task<string> PlaceAsync(string symbol, int quantity);
    int Count();
}
```

It shares no assembly with the container. `RemoteHost` resolves a service by
`typeof(T).FullName`, so what binds the two sides is the interface's **name and
shape**, not a common reference — which lets a front end declare exactly the
operations it calls and nothing else. That is the Remote Facade idea applied
properly, and it is why the namespace matters as much as the type name.

What makes these more than ordinary Playwright tests is that the test reaches
**the same facade the UI talks to**:

```csharp
await page.GetByTestId("place").ClickAsync();
await Assertions.Expect(page.GetByTestId("reference")).ToContainTextAsync("LSE-");

var book = await fixture.Facade.GetAsync<IOrderBook>();
Assert.Equal(1, book.Count());          // domain state, not rendered HTML
```

A page can show the right text for the wrong reason. The object graph cannot.
`ResetAsync()` between tests gives each one an empty order book without
restarting anything — and the UI's proxies survive it, because they hold the
service name rather than an instance.

## What each test demonstrates

| Test | Point |
|---|---|
| `Calling_a_remote_instance_looks_local` | The basic shape |
| `A_record_round_trips` | Data crosses cleanly |
| `A_synchronous_method_works_unchanged` | No reshaping needed; `int`, not `Task<int>` |
| `Each_container_gets_its_own_configuration` | Typed options, per container, from one startup |
| `Clicking_place_puts_a_real_order_in_the_container` | A browser click reaching the real domain graph |
| `The_refresh_button_updates_without_a_navigation` | Playwright waiting on dynamic content |
| `A_domain_rejection_is_rendered_with_the_domain_s_own_message` | An exception surviving three hops to the page |
| `Two_facades_share_one_object_graph` | One container, one graph, two surfaces |
| `A_second_startup_substitutes_a_dependency` | Substitution = another startup, in C# |
| `An_exception_survives_the_boundary` | Failures keep their message |
| `Reset_gives_each_test_a_clean_graph` | Per-test isolation without a new container |
| `An_unregistered_facade_fails_immediately_and_says_what_exists` | Mistakes fail early and name the fix |

## The one rule to internalise

**Arguments and return values cross by value, never by reference.**

A data record crosses cleanly. An object *with methods* also crosses — the
container has the type, so it deserializes the state and runs the real methods —
but on a **copy**. Mutations made inside the container never come back, with no
error to tell you. An interface argument fails loudly instead:
`Deserialization of interface or abstract types is not supported`.

That is why the facade pattern is the recommendation and not a style preference.
A narrow interface taking simple values keeps the interesting objects inside the
container, where the startup built them, instead of copying them across a
boundary that cannot carry their identity.

If you need a live object that calls *back* into the test — a progress handler,
a mock you want to `Verify()` — that was `LIB_CALLBACKS`. It was removed in v3
and preserved on the host repo's `callbacks` branch. Substitute with a second
startup instead, as `FixedClockStartup` does here.

## How the image is referenced

The fixture names the image the way any consumer would, and knows nothing about
a Dockerfile:

```csharp
private const string HostImage = "ghcr.io/drewlane-dev/remote-facade-host:3.2.0";

var container = new ContainerBuilder()
    .WithImage(HostImage)
    .WithRemoteFacade(startup, pluginDir, transport: PluginTransport.Copy)
    .WithOptions(new OrderBookOptions { Venue = venue })
    .Build();
```

Pinned exactly, not to `3`: this demo shows behaviour from specific releases,
and floating on a major would silently accept a version it has never been run
against.

`PluginTransport.Copy` rather than a bind mount, deliberately. A bind mount
names a path on the Docker **host**, so it breaks the moment the test itself
runs in a container — which is exactly what a containerised CI runner does. The
container would get an empty directory and fail with "assembly not found",
naming the file but not the reason.

The extensions come from **`RemoteFacade.Testcontainers`**, a separate package
from `RemoteFacade.Client` so the client stays free of any container
dependency. Reference both: NuGet does not flow build assets through a
transitive dependency, and the client ships the MSBuild target below.

## Two test layers, fanned out over runners

The application is a real stack, and each layer tests it at a different seam:

| Suite | Project | What it starts | What belongs there |
|---|---|---|---|
| `integration` | `OrderBook.IntegrationTests` | SQL Server + facade containers | behaviour: faster, failures point at one component, and it can assert things a page never shows |
| `e2e` | `OrderBook.E2ETests` | SQL Server + API + Angular + browser | only what a browser can prove: rendering, routing, script |

```
   Playwright ──▶ Angular (nginx) ──▶ API ──┐
                                             ├──▶ SQL Server
   integration tests ──▶ facade container ──┘
```

Both layers run **the same domain assembly** and the same `AddOrderBook`
wiring, so a bug cannot pass in one and fail in the other. The facade container
loads it as a plugin published by `RemoteFacade.Client`'s MSBuild target; the
API references it directly.

`OrderBook.slnx` holds every project, and `integration.slnf` / `e2e.slnf` are
solution filters so a runner builds only what its layer needs.

**A suite is a `.slnf`**, and CI packs one per call:

```powershell
scripts/suites.ps1 -Sln integration.slnf -Tags domain,graph
scripts/suites.ps1 -Sln e2e.slnf         -Tags journey
```

The split is expressed in `[TestCategory]` tags on the test classes, one leg
per tag. That is not a style choice: **MSTest cannot list test class names
without running the tests** — `--list-tests` reports method names only, and
`--report-trx` is rejected alongside it — so classes are not addressable ahead
of a run. Tags are, and `--filter` works during discovery, which turns out to
be the better primitive anyway.

`e2e` is one tag deliberately: its tests share a browser, API, web and SQL
stack, and standing that up is 94–99% of the leg's wall-clock. Split across
legs they would each rebuild all of it. Split where the fixture is cheap; pack
where it is expensive.

`-MaxParallel` packs several tags onto one leg by OR-ing them into a single
filter, so the cap still means "at most this many legs".

**Every test must carry exactly one of the tags**, and the script proves it
before emitting a matrix — without running anything:

```
OrderBook.IntegrationTests has 4 test(s) carrying none of: domain.
They would run on no leg. Tag them, or add their tag to -Tags.
```

Both directions are checked, and the second only means something because of
the first. An untagged test and a double-tagged one cancel out exactly in a
sum-versus-total check — 4 + 2 = 6 = total, while one test runs nowhere and
another runs twice — so the uncovered count is queried directly rather than
inferred from arithmetic.

**Granularity** offers a coarser option:

```powershell
scripts/suites.ps1 -Sln integration.slnf -Granularity Project      # by csproj
```

`Project` emits one leg per test project with no filter at all, and never runs
a discovery pass. `-MaxParallel` does not apply there: a leg runs one
executable, so the leg count *is* the project count and cannot be capped below
it. Passing both is refused rather than quietly ignored.

Which projects inside the filter are runners is asked of MSBuild —
`IsTestingPlatformApplication` — rather than matched by name. Names do not
discriminate here: `e2e.slnf` also contains `OrderBook.Api`, which is also an
`Exe`, and `OrderBook.Tests.Shared`, which is also called `*Tests`. A name
pattern would have to be kept correct by hand as projects are added and
renamed; this cannot go stale.

The filter is already what each leg builds, so the projects that get built and
the projects that get run cannot drift apart.

A filter holding no test project is fatal, because a suite that runs nothing is
indistinguishable from one that passes.

```
no project in src-only.slnf is a test project (IsTestingPlatformApplication).
Projects in it:
  OrderBook.Domain
  OrderBook.Api
```

## Where the code lives

Everything here — the application, the startup, the facade and the tests — is in
**one project**. The container loads the test assembly's own output directory:

```csharp
var plugin = AppContext.BaseDirectory;
```

That is the simplest arrangement, and the right one when the startup and facade
are test scaffolding you want sitting next to the tests that use them.

**What it costs.** The whole test output is copied into every container: here
that is 13 MB and 19 assemblies — MSTest, Testcontainers, Docker.DotNet and the
rest — against 216 KB and 4 for the application alone. It also puts
test-framework assemblies on the container's assembly-resolution path. Fine for
a handful of containers; worth avoiding if you start many, or if you want a hard
guarantee that nothing test-only can load inside the container.

**The alternative: a separate project.** Put the startup and facade in their own
small library, and `RemoteFacade.Client`'s MSBuild target publishes it for you:

```xml
<RemoteFacadePlugin Include="..\TestSupport\TestSupport.csproj" />
```

which produces `$(OutDir)plugin/TestSupport/`, and the fixture maps that instead
of `AppContext.BaseDirectory`. Same code, 216 KB payload.

**What does NOT work:** pointing `<RemoteFacadePlugin>` at the project that
contains it. The target runs after `Build` and needs `Publish`, which needs
`Build`, so MSBuild refuses with `MSB4006: circular dependency`. If the code
lives in your test project, use `AppContext.BaseDirectory` — do not try to make
the project publish itself.

## Publishing your own application image

This demo copies the application into a stock host container at test time, which
keeps the loop fast and needs no image of your own. In production you may prefer
to bake the application in:

```dockerfile
FROM ghcr.io/drewlane-dev/remote-facade-host:3.2.0
COPY publish/ /plugin/
ENV LIB_DIR=/plugin \
    LIB_ASSEMBLY=OrderBook.dll \
    LIB_REGISTRAR=OrderBook.DemoStartup.Configure
```

Then the fixture references your image and drops the plugin transport
entirely. The trade is a build step per change against a
self-contained artifact you can push to a registry.
