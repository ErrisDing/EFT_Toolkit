using System.Diagnostics;

namespace EftToolkit.App.Lifecycle;

/// <summary>
/// Opens something with the user's default handler: a folder, or a Windows settings page.
/// </summary>
/// <remarks>
/// <para>
/// <c>UseShellExecute</c> is what makes this the shell's decision rather than a process launch — a
/// <c>ms-settings:</c> target is a URL the shell knows how to route, and a directory is something
/// the shell knows how to show. Nothing here waits for what it started, and a failure is left to
/// propagate: the command that called it reports it into the panel, where the user can see it.
/// </para>
/// <para>
/// A <see langword="null"/> return is deliberately not treated as a failure. It means the shell
/// handled the target without starting a process of its own, which is what happens every time a
/// <c>ms-settings:</c> page is opened while the Settings process is already running. A target the
/// shell cannot route at all does not come back as null either — it throws — so there is nothing for
/// a null check to catch, and checking it turns a page that opened into a message saying it did not.
/// </para>
/// </remarks>
public static class ShellLauncher
{
    public static void Open(string target)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(target);

        // Disposed rather than waited for, when there is one at all. The process is someone else's,
        // and the panel must not hold a handle on it until it exits.
        using Process? process = Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
    }
}
