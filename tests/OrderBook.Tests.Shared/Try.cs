namespace OrderBook.Tests;

/// <summary>
/// Captures an exception instead of asserting a type, replacing xUnit's
/// Record.ExceptionAsync.
///
/// The tests using it assert on the MESSAGE, not the type: what is being
/// checked is that a failure crossed the remote boundary intact. Asserting a
/// type here would pass on a transport error that happened to be the same
/// type, which is the thing these tests exist to rule out.
/// </summary>
public static class Try
{
    public static async Task<Exception?> ExceptionAsync(Func<Task> action)
    {
        try
        {
            await action();
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }
}
