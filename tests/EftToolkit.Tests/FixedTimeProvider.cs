namespace EftToolkit.Tests;

/// <summary>Deterministic clock so file names and timestamps are asserted exactly.</summary>
public sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => utcNow;
}
