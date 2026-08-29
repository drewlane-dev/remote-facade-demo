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

Tests are grouped into **suites** — environments that can run on their own
runner, in their own containers, with no shared state:

| Suite | What it starts | What belongs there |
|---|---|---|
| `integration` | one facade container | behaviour: it is faster, its failures point at one component, and it can assert things a page never shows |
| `e2e` | facade + web app + browser | only what a browser can prove: rendering, navigation, script |

Both layers use **one definition of the backend** ([`Backend.cs`](tests/OrderBook.Tests/Backend.cs)),
so they cannot drift on image tag, options or plugin transport — and an e2e
failure therefore tells you the UI is wrong rather than the environment.

The boundary between them is the **fixture, not the test count**. With container
fixtures the wall-clock is dominated by starting the environment, so an even
split by test count would have every runner pay the same setup for a fraction
of the work.

```csharp
[Trait(Suites.Name, Suites.E2E)]
[Collection(WebUiCollection.Name)]
public class NavigationTests { ... }
```

[`.github/workflows/ci.yml`](.github/workflows/ci.yml) discovers them from the
**built assembly** and fans each stage out over its classes:

```bash
scripts/suites.py <test-exe>
# {"e2e": ["...NavigationTests", "...OrderPlacementTests"],
#  "integration": ["...GraphTests", "...ProtocolTests"]}
```

Nothing lists the classes in YAML. A hand-maintained matrix drifts the moment
someone adds a test class, and it drifts *silently* — the class runs on no
runner, every leg stays green, and nothing reports that coverage shrank. So the
script **fails rather than emitting a matrix**:

```
These test classes declare no [Trait(Suites.Name, ...)], so no runner would take them:
  OrderBook.Tests.OrphanProbeTests
```

### Controlling how many runners

`--max-parallel` caps the legs **per suite** and packs classes into them:

```bash
scripts/suites.py <exe> --max-parallel default=2,e2e=1
```

Six classes at `default=3` become three legs, balanced by test count — a class
is never split, so one huge class simply gets its own leg.

Packing is not just about runner count. Classes on one leg run in a **single
process**, so classes sharing a collection share **one fixture** — which is the
cost that dominates a container suite. Measured here: the two e2e classes as
separate legs took 95s and 91s, almost entirely fixture setup, against ~95s for
both packed together. Splitting them cost an extra runner and saved nothing.

The rule of thumb: split by class where the fixture is cheap, pack where it is
expensive. That is why this repo runs `default=2,e2e=1`.

### Balancing by measured runtime

Each leg writes xUnit XML; a final job folds it into a rolling `timings.json`
(exponentially weighted, so a class that gets slower shows up within a few runs)
and caches it. The next run balances by recorded runtime instead of test count.

The subtlety is that **a leg has two costs**, and only one is per-class:

```
leg cost  ≈  fixture setup (once per leg)  +  Σ test time of its classes
```

Measured here, the fixture is **94–99%** of a leg:

```
integration leg:  5.8s wall |  0.07s test time |  5.7s fixture
e2e leg:         11.8s wall |  0.70s test time | 11.1s fixture
```

So weighting by test time alone would balance the remaining few percent. The
script reports what a split actually buys:

```
e2e: 2 leg(s), slowest ~11.7s (fixture 11.2s + tests)
    vs 1 leg at ~11.9s: saves 0.2s for 1 extra runner(s) — probably not worth it
```

A class with no history is weighted at the **median** of the known ones, not
zero — zero would make every new class look free and pile them onto one leg.

Each leg builds its own containers; Testcontainers randomises names, networks
and host ports, so nothing needs coordinating between runners. The same script
serves Azure DevOps with `--ado`, which wants a flat matrix object rather than
per-stage arrays.

## Where the code lives

Everything here — the application, the startup, the facade and the tests — is in
**one project**. The container loads the test assembly's own output directory:

```csharp
var plugin = AppContext.BaseDirectory;
```

That is the simplest arrangement, and the right one when the startup and facade
are test scaffolding you want sitting next to the tests that use them.

**What it costs.** The whole test output is copied into every container: here
that is 13 MB and 19 assemblies — xunit, Testcontainers, Docker.DotNet and the
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
