# remote-class-demo

A small, runnable demonstration of hosting a **composition root** with
[`remote-class-host`](https://github.com/drewlane-dev/remote-class-host): your
real application code runs inside a container, and your tests drive it through a
narrow interface as if it were local.

```bash
./build-host-image.sh                      # once, until v1.1.0 is published
dotnet run --project tests/OrderBook.Tests
```

Eight tests, about three seconds, two containers. Docker is the only
prerequisite.

## The two files that matter

Everything else is scaffolding. The pattern is these two.

**1. A startup — all the wiring, in C#** ([`DemoStartup.cs`](src/OrderBook/DemoStartup.cs)):

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

**2. A facade — the surface the test drives** ([`OrderBook.cs`](src/OrderBook/OrderBook.cs)):

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
foreach (var (key, value) in RemoteHostEnvironment.For(typeof(DemoStartup)))
    builder = builder.WithEnvironment(key, value);
```

That produces `LIB_ASSEMBLY=OrderBook.dll` and
`LIB_REGISTRAR=OrderBook.DemoStartup.Configure`. There is no `LIB_TYPE`, no
`LIB_OPTIONS`, no `LIB_SERVICES` — the startup says all of it in C#, where a
factory registration handles any constructor shape the language allows.

## What each test demonstrates

| Test | Point |
|---|---|
| `Calling_a_remote_instance_looks_local` | The basic shape |
| `A_record_round_trips` | Data crosses cleanly |
| `A_synchronous_method_works_unchanged` | No reshaping needed; `int`, not `Task<int>` |
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

When you genuinely need a live object that calls *back* into the test — a
progress handler, a mock you want to `Verify()` — that is `LIB_CALLBACKS`, which
passes a reference rather than data and works for interfaces only.

## How the image is referenced

The fixture names the image the way any consumer would, and knows nothing about
a Dockerfile:

```csharp
private const string HostImage = "ghcr.io/drewlane-dev/remote-class-host:1.1.0";

var builder = new ContainerBuilder()
    .WithImage(HostImage)
    .WithResourceMapping(new DirectoryInfo(pluginDir), "/plugin")
    .WithEnvironment("LIB_DIR", "/plugin")
    ...
```

Pinned to the MINOR version deliberately. Composition-root hosting arrived in
1.1; pinning to `1` would silently accept a future 1.x this demo has not been
checked against.

**The one temporary step.** That tag is not on the registry yet, because v1.1.0
is unpublished. `build-host-image.sh` builds it locally under the same name, so
Docker finds it and never reaches for the registry.

When v1.1.0 ships, **delete `build-host-image.sh`**. Nothing else changes — the
demo already asks for the published tag, and the image is simply pulled.

The client is still referenced by project path for the same reason. Once the
package is on nuget.org, swap that `ProjectReference` for:

```xml
<PackageReference Include="RemoteClass.Client" Version="1.1.*" />
```

## Publishing your own application image

This demo copies the application into a stock host container at test time, which
keeps the loop fast and needs no image of your own. In production you may prefer
to bake the application in:

```dockerfile
FROM ghcr.io/drewlane-dev/remote-class-host:1.1.0
COPY publish/ /plugin/
ENV LIB_DIR=/plugin \
    LIB_ASSEMBLY=OrderBook.dll \
    LIB_REGISTRAR=OrderBook.DemoStartup.Configure
```

Then the fixture references your image and drops the resource mapping and the
environment loop entirely. The trade is a build step per change against a
self-contained artifact you can push to a registry.
