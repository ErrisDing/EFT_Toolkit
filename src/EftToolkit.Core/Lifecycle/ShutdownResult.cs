namespace EftToolkit.Core.Lifecycle;

/// <summary>
/// The names of the shutdown steps, in the order they run.
/// </summary>
/// <remarks>
/// The order is the contract, not an implementation detail. Commands are refused first so nothing
/// new can start; audio is bypassed before it is stopped so the teardown is not heard; the display
/// is restored before the shortcuts that drive it are released; and the configuration and the log
/// are written last, when nothing else can change them. The names are stable strings because they
/// appear in the log, where a person reads them next to the failure they belong to.
/// </remarks>
public static class ShutdownSteps
{
    public const string RejectCommands = "reject-commands";

    public const string AudioBypass = "audio-bypass";

    public const string AudioDisable = "audio-disable";

    public const string DisplayDisable = "display-disable-and-restore";

    public const string HotkeysUnregister = "hotkeys-unregister";

    public const string PlatformEventsStop = "platform-events-stop";

    public const string SettingsSave = "settings-save";

    public const string LoggerFlush = "logger-flush-and-dispose";

    /// <summary>Every step, in order.</summary>
    public static IReadOnlyList<string> Ordered { get; } =
    [
        RejectCommands,
        AudioBypass,
        AudioDisable,
        DisplayDisable,
        HotkeysUnregister,
        PlatformEventsStop,
        SettingsSave,
        LoggerFlush,
    ];
}

/// <summary>
/// One shutdown step that did not do its job.
/// </summary>
/// <param name="Step">One of <see cref="ShutdownSteps"/>.</param>
/// <param name="Message">Why it failed, or that it never ran.</param>
/// <param name="TimedOut">
/// Whether the step failed to complete inside the shutdown window. A step the deadline cut off
/// before it ran is reported this way too, because "it did not happen" is the part that matters.
/// </param>
/// <param name="Exception">The fault, when there was one.</param>
public sealed record ShutdownFailure(
    string Step,
    string Message,
    bool TimedOut = false,
    Exception? Exception = null);

/// <summary>
/// What a shutdown did. It is returned rather than thrown: shutdown runs when the application is
/// already on its way out, and a caller that cannot do anything about a failure still needs to see
/// what went wrong.
/// </summary>
public sealed record ShutdownResult(
    DateTimeOffset StartedAtUtc,
    DateTimeOffset CompletedAtUtc,
    IReadOnlyList<ShutdownFailure> Failures)
{
    /// <summary>
    /// Whether the deadline is what ended the shutdown, in whole or in part. A shutdown that ran out
    /// of time still returns, and what it managed to do is what <see cref="Failures"/> describes.
    /// </summary>
    public bool TimedOut => Failures.Any(failure => failure.TimedOut);

    public bool Succeeded => Failures.Count == 0;

    public TimeSpan Duration => CompletedAtUtc - StartedAtUtc;
}
