namespace EftToolkit.Tests.TestSupport;

/// <summary>Waits for a condition that a background loop is expected to bring about.</summary>
internal static class AsyncWait
{
    /// <summary>How long to wait before calling a condition unreachable.</summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(10);

    internal static async Task UntilAsync(Func<bool> condition, string because)
    {
        DateTime deadline = DateTime.UtcNow + Timeout;

        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(Poll).ConfigureAwait(false);
        }

        Assert.Fail($"Timed out waiting until {because}.");
    }
}
