namespace OrderBook.Tests;

/// <summary>
/// The parallelisable units of this suite.
///
/// A suite is a set of tests that can run on its OWN agent, in its own
/// containers, with no shared state. The boundary is the fixture, not the test
/// count -- with container fixtures the wall-clock cost is dominated by
/// starting the environment, so splitting evenly by test count would have every
/// agent pay the same setup for a fraction of the work.
///
/// These names are the single source of truth. The pipeline discovers them from
/// the built assembly rather than repeating them in YAML, so adding a suite
/// here is all it takes -- and a test class that names no suite fails the
/// build rather than silently running on no agent.
/// </summary>
public static class Suites
{
    public const string Name = "Suite";

    /// <summary>
    /// Integration: the backend alone, driven through the facade. No browser,
    /// no UI. This is where behaviour belongs -- it is faster, its failures
    /// point at one component, and it can assert things a page never shows.
    /// </summary>
    public const string Integration = "integration";

    /// <summary>
    /// End to end: the same backend with a web app and a browser in front.
    /// Reserve it for what only a browser can prove -- rendering, navigation,
    /// script. Anything assertable without one belongs in Integration, where
    /// it costs a fraction as much and fails more precisely.
    /// </summary>
    public const string E2E = "e2e";
}
