namespace EftToolkit.Audio.Processes;

/// <summary>
/// Reads the process IDs currently running under an executable name.
/// </summary>
/// <remarks>
/// The production implementation reads only what <see cref="System.Diagnostics.Process"/> exposes
/// for a process it already knows exists: its ID and its image name. Modules, handles, memory, and
/// command lines are never inspected.
/// </remarks>
public interface IProcessSnapshotProvider
{
    /// <summary>The IDs running under <paramref name="executableName"/>, as a bare file name.</summary>
    IReadOnlySet<int> GetProcessIds(string executableName);
}
