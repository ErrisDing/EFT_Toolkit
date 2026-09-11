using System.Globalization;
using System.Text.Json;
using EftToolkit.Core.Diagnostics;

namespace EftToolkit.Core.Configuration;

/// <summary>
/// Versioned JSON configuration in the user's application-data directory.
/// Writes go to a temporary file that is flushed to disk and then moved over the target, so an
/// interrupted save cannot truncate the previous settings. A file that cannot be read is renamed
/// aside for diagnosis rather than deleted, because it is the only evidence of what went wrong.
/// </summary>
public sealed class JsonOptionsStore : IOptionsStore
{
    public const string FileName = "settings.json";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private readonly string _directory;
    private readonly string _settingsPath;
    private readonly string _temporaryPath;
    private readonly TimeProvider _timeProvider;
    private readonly IAppLogger? _logger;

    public JsonOptionsStore(string directory, TimeProvider timeProvider, IAppLogger? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _directory = directory;
        _settingsPath = Path.Combine(directory, FileName);
        _temporaryPath = _settingsPath + ".tmp";
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>Production root: <c>%LOCALAPPDATA%\EftToolkit</c>.</summary>
    public static string DefaultRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "EftToolkit");

    public static JsonOptionsStore CreateDefault(IAppLogger? logger = null) =>
        new(DefaultRoot, TimeProvider.System, logger);

    public async Task<ToolkitOptions> LoadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!File.Exists(_settingsPath))
        {
            return ToolkitOptions.CreateDefault();
        }

        try
        {
            ToolkitOptions? loaded;
            await using (FileStream stream = File.OpenRead(_settingsPath))
            {
                loaded = await JsonSerializer.DeserializeAsync<ToolkitOptions>(stream, SerializerOptions, cancellationToken)
                    .ConfigureAwait(false);
            }

            if (loaded is null)
            {
                return PreserveCorruptFile("the file contained no configuration object");
            }

            if (loaded.SchemaVersion != ToolkitOptions.CurrentSchemaVersion)
            {
                return PreserveCorruptFile(
                    string.Create(CultureInfo.InvariantCulture, $"schema version {loaded.SchemaVersion} is not supported"));
            }

            // Valid JSON with out-of-range values is repaired in memory. The file stays in place:
            // the user's other settings are still meaningful, so replacing it would lose more than it fixes.
            return OptionsValidator.Validate(loaded);
        }
        catch (JsonException exception)
        {
            return PreserveCorruptFile(exception.Message);
        }
        catch (NotSupportedException exception)
        {
            return PreserveCorruptFile(exception.Message);
        }
    }

    public async Task SaveAsync(ToolkitOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        cancellationToken.ThrowIfCancellationRequested();

        Directory.CreateDirectory(_directory);

        await using (FileStream stream = new(_temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await JsonSerializer.SerializeAsync(stream, options, SerializerOptions, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);

            // Durability before the move: a rename over a flushed file is atomic, a rename over a
            // buffered one can leave an empty settings file after a power loss.
            stream.Flush(flushToDisk: true);
        }

        File.Move(_temporaryPath, _settingsPath, overwrite: true);
    }

    private ToolkitOptions PreserveCorruptFile(string reason)
    {
        string stamp = _timeProvider.GetUtcNow().UtcDateTime.ToString("yyyyMMdd-HHmmssfff", CultureInfo.InvariantCulture);
        string preservedPath = Path.Combine(_directory, $"settings.corrupt.{stamp}.json");

        try
        {
            File.Move(_settingsPath, preservedPath, overwrite: true);
            _logger?.Write(
                LogLevel.Warning,
                "settings.corrupt",
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
                "settings.corrupt.preserveFailed",
                new Dictionary<string, object?> { ["reason"] = reason },
                exception);
        }

        return ToolkitOptions.CreateDefault();
    }
}
