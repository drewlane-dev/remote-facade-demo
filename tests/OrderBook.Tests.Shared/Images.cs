namespace OrderBook.Tests;

/// <summary>
/// How a fixture learns which image to run.
///
/// CI builds the images once, pushes them, and passes each one back as a
/// DIGEST reference. The fixtures then run exactly what this run produced,
/// rather than rebuilding per leg and hoping the results match.
///
/// Unset means a developer's machine: build locally, which is what makes the
/// tests runnable without a registry.
/// </summary>
public static class Images
{
    /// <summary>
    /// The image reference in <paramref name="variable"/>, or null when it is
    /// unset.
    ///
    /// A TAG is rejected rather than accepted. The whole point of passing the
    /// reference in is to know which bytes were tested, and a tag can be moved
    /// between the push and the pull -- by a concurrent run of this same
    /// pipeline, most obviously. Accepting one would leave the property this
    /// exists to provide silently untrue.
    /// </summary>
    public static string? Pinned(string variable)
    {
        var value = Environment.GetEnvironmentVariable(variable);

        if (string.IsNullOrWhiteSpace(value)) return null;

        if (!value.Contains("@sha256:", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"{variable} is \"{value}\", which is a tag rather than a digest. " +
                "Pass name@sha256:... so the tests provably run the image this " +
                "pipeline built, or leave it unset to build locally.");
        }

        return value;
    }
}
