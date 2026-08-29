using DotNet.Testcontainers.Builders;
using RemoteFacadeHost.Client;

namespace OrderBook.Tests;

/// <summary>
/// One definition of the backend, used by both test layers.
///
/// The integration layer starts it alone. The e2e layer starts the same thing
/// and puts a web app and a browser in front. Defining it twice would let the
/// two layers drift -- a different image tag, a different venue, a different
/// plugin transport -- and then an e2e failure would not tell you whether the
/// UI or the environment was at fault.
/// </summary>
internal static class Backend
{
    /// <summary>
    /// Pinned exactly, not to a major: this demo shows behaviour from specific
    /// releases, and floating would silently accept a version it has never run
    /// against.
    /// </summary>
    public const string Image = "ghcr.io/drewlane-dev/remote-facade-host:3.2.0";

    /// <summary>
    /// The application IS the test assembly, so the container loads this
    /// project's own output directory. Nothing has to be published first.
    /// </summary>
    public static string PluginDir
    {
        get
        {
            var dir = AppContext.BaseDirectory;

            if (!File.Exists(Path.Combine(dir, "OrderBook.Tests.dll")))
            {
                throw new InvalidOperationException(
                    $"expected this test assembly at {dir}, which is also the folder the " +
                    "container loads.");
            }

            return dir;
        }
    }

    /// <summary>
    /// A facade container running <paramref name="startup"/>, unstarted so a
    /// caller can attach a network or add environment of its own.
    /// </summary>
    public static ContainerBuilder For(Type startup, string venue) =>
        new ContainerBuilder()
            .WithImage(Image)
            // Copy, not a bind mount. A bind mount names a path on the Docker
            // HOST, so it breaks the moment the tests run inside a container
            // themselves -- the container would get an empty directory and fail
            // with "assembly not found", naming the file but not the reason.
            .WithRemoteFacade(startup, PluginDir, transport: PluginTransport.Copy)
            // Typed, not a string. Rename Venue and this stops compiling;
            // misspell an environment variable and nothing would have.
            .WithOptions(new OrderBookOptions { Venue = venue });
}
