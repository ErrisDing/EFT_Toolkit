using System.Diagnostics.CodeAnalysis;

namespace EftToolkit.Audio.Processes;

/// <summary>Executable names as the toolkit stores and matches them.</summary>
internal static class ProcessNames
{
    /// <summary>Directory separators, plus the colon that would make a name look like a drive.</summary>
    private static readonly char[] PathCharacters = ['\\', '/', ':'];

    /// <summary>
    /// Reduces a configured executable name to the bare image name used for comparisons. A name
    /// carrying a directory, or one that reduces to nothing, cannot name a running image, so it is
    /// refused rather than quietly matched against something other than what the user picked.
    /// </summary>
    internal static bool TryNormalize(string? executableName, [NotNullWhen(true)] out string? normalized)
    {
        normalized = null;

        if (string.IsNullOrWhiteSpace(executableName))
        {
            return false;
        }

        string trimmed = executableName.Trim();

        if (trimmed.IndexOfAny(PathCharacters) >= 0)
        {
            return false;
        }

        string bare = Path.GetFileNameWithoutExtension(trimmed);

        if (string.IsNullOrWhiteSpace(bare))
        {
            return false;
        }

        normalized = bare;
        return true;
    }
}
