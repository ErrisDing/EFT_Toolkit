using System.Runtime.InteropServices;

namespace EftToolkit.Platform.Windows.Processes;

/// <summary>
/// Tells whether a running process is elevated, so a shortcut that is registered and then never
/// delivered can be explained rather than guessed at.
/// </summary>
/// <remarks>
/// A process without administrative rights cannot receive input while an elevated window has focus.
/// Windows enforces this in the user interface, below anything an application can observe:
/// <c>RegisterHotKey</c> succeeds, Windows holds the shortcut, and the <c>WM_HOTKEY</c> is simply
/// never sent. The toolkit sees a registration it cannot fault and a key that never arrives, which
/// is indistinguishable from a key that was never pressed.
/// </remarks>
internal static partial class ProcessElevationProbe
{
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint TokenQuery = 0x0008;

    /// <summary><c>TOKEN_INFORMATION_CLASS.TokenElevation</c>: a single <c>DWORD</c>, non-zero when elevated.</summary>
    private const int TokenElevation = 20;

    /// <summary>Whether this process itself is elevated, which is what decides whether it is blocked.</summary>
    public static bool IsCurrentProcessElevated() => IsElevated(unchecked((int)GetCurrentProcessId()));

    /// <summary>
    /// Names of the running processes, among those given, that are elevated.
    /// </summary>
    /// <remarks>
    /// A process that cannot be opened is reported as elevated rather than skipped: the only thing
    /// that stops an ordinary user from opening another process's token is that process being
    /// higher-privileged or protected, which is precisely the condition that matters here.
    /// </remarks>
    public static IReadOnlyList<string> FindElevatedProcesses(IEnumerable<string> executableNames)
    {
        List<string> elevated = [];

        foreach (string name in executableNames)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            foreach (System.Diagnostics.Process process in System.Diagnostics.Process.GetProcessesByName(name))
            {
                using (process)
                {
                    if (IsElevated(process.Id))
                    {
                        elevated.Add(process.ProcessName);
                    }
                }
            }
        }

        return elevated;
    }

    private static bool IsElevated(int processId)
    {
        nint process = OpenProcess(ProcessQueryLimitedInformation, inheritHandle: false, processId);

        if (process == 0)
        {
            return true;
        }

        try
        {
            if (!OpenProcessToken(process, TokenQuery, out nint token))
            {
                return true;
            }

            try
            {
                return GetTokenInformation(token, TokenElevation, out uint elevated, sizeof(uint), out _)
                    && elevated != 0;
            }
            finally
            {
                _ = CloseHandle(token);
            }
        }
        finally
        {
            _ = CloseHandle(process);
        }
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, int processId);

    [LibraryImport("kernel32.dll")]
    private static partial uint GetCurrentProcessId();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenProcessToken(nint process, uint desiredAccess, out nint token);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetTokenInformation(nint token, int informationClass, out uint information, uint informationLength, out uint returnedLength);
}
