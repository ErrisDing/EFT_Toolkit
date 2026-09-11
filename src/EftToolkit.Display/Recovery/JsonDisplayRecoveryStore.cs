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
/// <para>
/// Writes are serialized against each other: the module persists from the display worker and from a
/// topology refresh, so two concurrent saves would otherwise collide rather than one simply winning
/// the race.
/// </para>
/// <para>
/// That gate only covers one instance, and the file is not private to one. Each save therefore writes
/// to a temporary name of its own and moves that into place, so a save that overlaps another store's
/// save is a last-writer-wins race rather than a failed write.
/// </para>
/// </remarks>
public sealed class JsonDisplayRecoveryStore : IDisplayRecoveryStore
{
    public const string FileName = "display-recovery.json";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    /// <summary>
    /// How many times the completed temporary file is offered to its final name before giving up.
    /// </summary>
    /// <remarks>
    /// The move can be refused for reasons that have nothing to do with this process: a virus scanner
    /// opens a file the moment it is written, and a backup or search indexer can be holding the
    /// destination. Those holds last milliseconds, and retrying is what turns a transient one into a
    /// slower save rather than into a failed one.
    /// </remarks>
    private const int MoveAttempts = 20;

    private const int MoveRetryDelayMilliseconds = 25;

    private readonly string _directory;
    private readonly string _path;
    private readonly TimeProvider _timeProvider;
    private readonly IAppLogger? _logger;

    /// <summary>Distinguishes this store's temporary files from another store's over the same directory.</summary>
    private readonly string _instance = Guid.NewGuid().ToString("N");

    /// <summary>Serializes <see cref="SaveAsync"/> and <see cref="RemoveAsync"/> against each other.</summary>
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    /// <summary>Makes each save's temporary file name distinct. Incremented under <see cref="_writeGate"/>.</summary>
    private int _saves;

    public JsonDisplayRecoveryStore(string directory, TimeProvider timeProvider, IAppLogger? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _directory = directory;
        _path = Path.Combine(directory, FileName);
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

        // Unique per save, and not one fixed name for the store's lifetime. The write gate only
        // serializes this instance, and the file is not private to one: a second store over the same
        // directory - another window, a run resuming what the previous one left behind - writes the
        // same file. Sharing one temporary name between them is a sharing violation exactly when the
        // two saves overlap, which on the build machine is what failed the recovery tests.
        //
        // Both parts are needed. The instance token separates stores, which a counter cannot do
        // because every store counts from zero; the counter separates saves within one store, which
        // the token alone cannot do either.
        string temporaryPath = string.Create(
            CultureInfo.InvariantCulture,
            $"{_path}.{_instance}.{_saves++}.tmp");

        try
        {
            await using (FileStream stream = new(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await JsonSerializer.SerializeAsync(stream, snapshot, SerializerOptions, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);

                // Durability before the move, for the same reason as the settings file: a rename over a
                // buffered file can leave an empty recovery file after a power loss.
                stream.Flush(flushToDisk: true);
            }

            await MoveIntoPlaceAsync(temporaryPath, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Whatever went wrong, the temporary file is not left behind: the next save uses a new
            // name, so a survivor here would sit in the user's directory for good.
            TryDeleteTemporaryFile(temporaryPath);
            throw;
        }
    }

    /// <summary>
    /// Replaces the recovery file with the completed temporary file, retrying a refusal.
    /// </summary>
    /// <remarks>
    /// <para>
    /// On Windows the move is a single atomic operation in the success case, but it is refused
    /// outright - rather than queued - if anything else holds the destination open for the moment it
    /// is attempted. A virus scanner inspecting the file just written is the usual culprit, and the
    /// observable symptom is a save that fails for no reason the user can act on.
    /// </para>
    /// <para>
    /// Both exception types are transient here, and which one arrives depends on what is holding the
    /// destination: a share violation is reported as <see cref="IOException"/> and a held file that
    /// cannot be deleted as <see cref="UnauthorizedAccessException"/>. Retrying only the first would
    /// leave the second failing exactly as before.
    /// </para>
    /// </remarks>
    private async Task MoveIntoPlaceAsync(string temporaryPath, CancellationToken cancellationToken)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(temporaryPath, _path, overwrite: true);
                return;
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException
                && attempt < MoveAttempts)
            {
                await Task.Delay(MoveRetryDelayMilliseconds, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static void TryDeleteTemporaryFile(string temporaryPath)
    {
        try
        {
            File.Delete(temporaryPath);
        }
        catch (IOException)
        {
            // Best effort. It is a uniquely named file, so leaving it does not affect a later save.
        }
        catch (UnauthorizedAccessException)
        {
            // Best effort. It is a uniquely named file, so leaving it does not affect a later save.
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
