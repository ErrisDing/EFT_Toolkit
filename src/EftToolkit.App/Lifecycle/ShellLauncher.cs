using System.Diagnostics;

namespace EftToolkit.App.Lifecycle;

/// <summary>
/// Opens something with the user's default handler: a folder, or a Windows settings page.
/// </summary>
/// <remarks>
/// <c>UseShellExecute</c> is what makes this the shell's decision rather than a process launch — a
/// <c>ms-settings:</c> target is a URL the shell knows how to route, and a directory is something
/// the shell knows how to show. Nothing here waits for what it started, and a failure is left to
/// propagate: the command that called it reports it into the panel, where the user can see it.
/// </remarks>
public static class ShellLauncher
{
    public static void Open(string target)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(target);

        using Process process = Process.Start(new ProcessStartInfo(target) { UseShellExecute = true })
            ?? throw new InvalidOperationException($"'{target}' could not be opened.");

        // Disposed rather than waited for. The process is someone else's, and the panel must not
        // hold a handle on it until it exits.
    }
}
