using System.Diagnostics;

namespace EftToolkit.Audio.Processes;

/// <summary>
/// Reads the process list through <see cref="Process"/>. Only the image name and the process ID are
/// read; nothing about the process internals is touched, and no handle is opened with rights beyond
/// what those two values need.
/// </summary>
public sealed class WindowsProcessSnapshotProvider : IProcessSnapshotProvider
{
    public IReadOnlySet<int> GetProcessIds(string executableName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executableName);

        HashSet<int> processIds = [];

        foreach (Process process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    // ProcessName is already the image name without its extension, which is why the
                    // configured name is normalized to the same shape before it gets here.
                    if (string.Equals(process.ProcessName, executableName, StringComparison.OrdinalIgnoreCase))
                    {
                        processIds.Add(process.Id);
                    }
                }
                catch (InvalidOperationException)
                {
                    // The process exited between being listed and being read. On a busy machine that
                    // is routine, and the next poll will not see it either.
                }
            }
        }

        return processIds;
    }
}
