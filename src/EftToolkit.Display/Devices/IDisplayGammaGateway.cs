using EftToolkit.Display.Gamma;

namespace EftToolkit.Display.Devices;

/// <summary>
/// The outcome of a gamma-ramp write. <see cref="ApiAccepted"/> reports what the driver said;
/// <see cref="ReadbackMatched"/> reports whether the value we read back is the value we asked for.
/// The two disagree on drivers that silently clamp, and that disagreement is what makes a write
/// untrustworthy even though it "succeeded".
/// </summary>
public sealed record GammaWriteResult(
    bool ApiAccepted,
    bool ReadbackMatched,
    int? Win32Error,
    string? Message)
{
    public bool Succeeded => ApiAccepted && ReadbackMatched;

    public static GammaWriteResult Accepted(int? win32Error = null, string? message = null) =>
        new(true, true, win32Error, message);

    public static GammaWriteResult Rejected(int win32Error, string message) =>
        new(false, false, win32Error, message);

    public static GammaWriteResult AcceptedButUnverified(string message) =>
        new(true, false, null, message);
}

/// <summary>
/// Reads and writes display gamma ramps. Every member is asynchronous because the underlying Win32
/// calls block on the display driver.
/// </summary>
public interface IDisplayGammaGateway
{
    Task<IReadOnlyList<DisplayDescriptor>> EnumerateAsync(CancellationToken cancellationToken);

    Task<GammaRamp> ReadAsync(DisplayDescriptor display, CancellationToken cancellationToken);

    Task<GammaWriteResult> WriteAsync(DisplayDescriptor display, GammaRamp ramp, CancellationToken cancellationToken);
}
