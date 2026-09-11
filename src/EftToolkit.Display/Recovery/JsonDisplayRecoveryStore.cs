using System.Globalization;
using System.Text.Json;
using EftToolkit.Core.Diagnostics;

namespace EftToolkit.Display.Recovery;

/// <summary>
/// Recovery snapshots as versioned JSON beside the settings file. An entry whose checksum does not
/// match its contents is dropped on load rather than failing the whole file: the remaining displays
/// are still recoverable, and discarding them would strand the user with changed ramps.
/// </summary>
/// <remarks>
/// Writes are serialized against each other. The module persists from the display worker and from a
/// topology refresh, and the temporary file has a fixed name, so two concurrent saves would
/// otherwise collide on it rather than one simply winning the race.
/// </remarks>
public sealed class JsonDisplayRecoveryStore : IDisplayRecoveryStore
{
    public const string FileName = "display-recovery.json";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private readonly string _directory;
    private readonly string _path;
    private readonly string _temporaryPath;
    private readonly TimeProvider _timeProvider;
    private readonly IAppLogger? _logger;

    /// <summary>Serializes <see cref="SaveAsync"/> and <see cref="RemoveAsync"/> against each other.</summary>
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    public JsonDisplayRecoveryStore(string directory, TimeProvider timeProvider, IAppLogger? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _directory = directory;
        _path = Path.Combine(directory, FileName);
        _temporaryPath = _path + ".tmp";
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>Production root, shared with the settings file: <c>%LOCALAPPDATA%\EftToolkit</c>.</summary>
    public static string DefaultRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "EftToolkit");

    public static JsonDisplayRecoveryStore CreateDefault(IAppLogger? logger = null) =>
        new(DefaultRoot, TimeProvider.System, logger);

    public async Task<DisplayRecoverySnapshot?> LoadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!File.Exists(_path))
        {
            return null;
        }

        DisplayRecoverySnapshot? loaded;

        try
        {
            await using (FileStream stream = File.OpenRead(_path))
            {
                loaded = await JsonSerializer
                    .DeserializeAsync<DisplayRecoverySnapshot>(stream, SerializerOptions, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (JsonException exception)
        {
            return PreserveCorruptFile(exception.Message);
        }
        catch (NotSupportedException exception)
        {
            return PreserveCorruptFile(exception.Message);
        }

        if (loaded is null)
        {
            return PreserveCorruptFile("the file contained no recovery snapshot");
        }

        if (loaded.SchemaVersion != DisplayRecoverySnapshot.CurrentSchemaVersion)
        {
            return PreserveCorruptFile(string.Create(
                CultureInfo.InvariantCulture,
                $"schema version {loaded.SchemaVersion} is not supported"));
        }

        List<DisplayRecoveryEntry> trusted = [];

        foreach (DisplayRecoveryEntry entry in loaded.Displays ?? [])
        {
            if (entry is null)
            {
                continue;
            }

            if (!entry.TryReadOriginalRamp(out _))
            {
                _logger?.Write(
                    LogLevel.Warning,
                    "display.recovery.entryRejected",
                    new Dictionary<string, object?> { ["displayId"] = entry.StableId });

                continue;
            }

            trusted.Add(entry);
        }

        return loaded with { Displays = trusted };
    }

    public async Task SaveAsync(DisplayRecoverySnapshot snapshot, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        cancellationToken.ThrowIfCancellationRequested();

        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await SaveCoreAsync(snapshot, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task RemoveAsync(IReadOnlySet<string> restoredStableIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(restoredStableIds);

        if (restoredStableIds.Count == 0)
        {
            return;
        }

        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            DisplayRecoverySnapshot? snapshot = await LoadAsync(cancellationToken).ConfigureAwait(false);

            if (snapshot is null)
            {
                return;
            }

            List<DisplayRecoveryEntry> remaining = [.. snapshot.Displays.Where(
                entry => !restoredStableIds.Contains(entry.StableId))];

            if (remaining.Count == snapshot.Displays.Count)
            {
                return;
            }

            if (remaining.Count == 0)
            {
                DeleteFile();
                return;
            }

            // The gate is already held, so the unlocked core is used rather than the public entry
            // point, which would deadlock waiting for itself.
            await SaveCoreAsync(snapshot with { Displays = remaining }, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>Writes the snapshot. The caller must hold <see cref="_writeGate"/>.</summary>
    private async Task SaveCoreAsync(DisplayRecoverySnapshot snapshot, CancellationToken cancellationToken)
    {
        if (snapshot.Displays.Count == 0)
        {
            // Nothing left to recover, so the file is removed rather than left claiming otherwise.
            DeleteFile();
            return;
        }

        Directory.CreateDirectory(_directory);

        try
        {
            await using (FileStream stream = new(_temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await JsonSerializer.SerializeAsync(stream, snapshot, SerializerOptions, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);

                // Durability before the move, for the same reason as the settings file: a rename over a
                // buffered file can leave an empty recovery file after a power loss.
                stream.Flush(flushToDisk: true);
            }

            File.Move(_temporaryPath, _path, overwrite: true);
        }
        catch
        {
            // A half-written temporary file would otherwise be mistaken for a live one on the next
            // save, which reuses this same path.
            TryDeleteTemporaryFile();
            throw;
        }
    }

    private void TryDeleteTemporaryFile()
    {
        try
        {
            File.Delete(_temporaryPath);
        }
        catch (IOException)
        {
            // Best effort: the next save recreates the file regardless.
        }
        catch (UnauthorizedAccessException)
        {
            // Best effort: the next save recreates the file regardless.
        }
    }

    private void DeleteFile()
    {
        try
        {
            File.Delete(_path);
        }
        catch (IOException exception)
        {
            _logger?.Write(LogLevel.Warning, "display.recovery.deleteFailed", exception: exception);
        }
        catch (UnauthorizedAccessException exception)
        {
            _logger?.Write(LogLevel.Warning, "display.recovery.deleteFailed", exception: exception);
        }
    }

    private DisplayRecoverySnapshot? PreserveCorruptFile(string reason)
    {
        string stamp = _timeProvider.GetUtcNow().UtcDateTime.ToString("yyyyMMdd-HHmmssfff", CultureInfo.InvariantCulture);
        string preservedPath = Path.Combine(_directory, $"display-recovery.corrupt.{stamp}.json");

        try
        {
            File.Move(_path, preservedPath, overwrite: true);
            _logger?.Write(
                LogLevel.Warning,
                "display.recovery.corrupt",
                new Dictionary<string, object?>
                {
                    ["preservedPath"] = preservedPath,
                    ["reason"] = reason,
                });
        }
        catch (IOException exception)
        {
            _logger?.Write(
                LogLevel.Error,
                "display.recovery.corrupt.preserveFailed",
                new Dictionary<string, object?> { ["reason"] = reason },
                exception);
        }

        return null;
    }
}
