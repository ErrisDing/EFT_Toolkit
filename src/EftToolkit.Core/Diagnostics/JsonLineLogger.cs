using System.Buffers;
using System.Text;
using System.Text.Json;

namespace EftToolkit.Core.Diagnostics;

/// <summary>
/// Appends one JSON object per line to a rolling file. Lines are self-contained so a torn write can
/// lose at most the line being written. Rotation happens before a write that would cross the
/// threshold, which keeps every file — including the live one — at or below the configured size.
/// </summary>
public sealed class JsonLineLogger : IAppLogger
{
    public const string FileName = "eft-toolkit.log";
    public const long DefaultMaxFileBytes = 5 * 1024 * 1024;
    public const int DefaultMaxArchives = 5;

    /// <summary>
    /// Keys whose values could contain captured audio. Compared ignoring case, because the point is
    /// to catch a mistake, not to match a spelling.
    /// </summary>
    private static readonly string[] ForbiddenPropertyKeys = ["audioSamples", "pcm", "bufferBytes"];

    private readonly string _directory;
    private readonly string _path;
    private readonly TimeProvider _timeProvider;
    private readonly long _maxFileBytes;
    private readonly int _maxArchives;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile bool _disposed;

    public JsonLineLogger(
        string directory,
        TimeProvider timeProvider,
        long maxFileBytes = DefaultMaxFileBytes,
        int maxArchives = DefaultMaxArchives)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxFileBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(maxArchives);

        _directory = directory;
        _path = Path.Combine(directory, FileName);
        _timeProvider = timeProvider;
        _maxFileBytes = maxFileBytes;
        _maxArchives = maxArchives;
    }

    /// <summary>Production root: <c>%LOCALAPPDATA%\EftToolkit\logs</c>.</summary>
    public static string DefaultRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "EftToolkit",
        "logs");

    public void Write(
        LogLevel level,
        string eventName,
        IReadOnlyDictionary<string, object?>? properties = null,
        Exception? exception = null)
    {
        if (_disposed)
        {
            return;
        }

        byte[] line = Format(level, eventName, properties, exception);

        _gate.Wait();
        try
        {
            if (_disposed)
            {
                return;
            }

            Directory.CreateDirectory(_directory);
            RotateIfNeeded(line.Length);

            using FileStream stream = new(_path, FileMode.Append, FileAccess.Write, FileShare.Read);
            stream.Write(line, 0, line.Length);
            stream.WriteByte((byte)'\n');
        }
        catch (IOException)
        {
            // Logging must never take the application down; a full or locked log file is not fatal.
        }
        catch (UnauthorizedAccessException)
        {
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        // Wait for an in-flight write so no line is torn, then mark closed. The semaphore itself is
        // intentionally not disposed: a writer already blocked on it would otherwise fault instead
        // of observing the closed flag.
        await _gate.WaitAsync().ConfigureAwait(false);
        _disposed = true;
        _gate.Release();
    }

    private byte[] Format(
        LogLevel level,
        string eventName,
        IReadOnlyDictionary<string, object?>? properties,
        Exception? exception)
    {
        ArrayBufferWriter<byte> buffer = new();
        using (Utf8JsonWriter writer = new(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("timestamp", _timeProvider.GetUtcNow());
            writer.WriteString("level", level.ToString());
            writer.WriteString("event", eventName);
            WriteProperties(writer, properties);
            WriteException(writer, exception);
            writer.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }

    private static void WriteProperties(Utf8JsonWriter writer, IReadOnlyDictionary<string, object?>? properties)
    {
        if (properties is null || properties.Count == 0)
        {
            return;
        }

        writer.WriteStartObject("properties");
        foreach ((string key, object? value) in properties)
        {
            if (IsForbiddenPropertyKey(key))
            {
                continue;
            }

            writer.WritePropertyName(key);
            if (value is null)
            {
                writer.WriteNullValue();
            }
            else
            {
                JsonSerializer.Serialize(writer, value, value.GetType());
            }
        }

        writer.WriteEndObject();
    }

    private static void WriteException(Utf8JsonWriter writer, Exception? exception)
    {
        if (exception is null)
        {
            return;
        }

        writer.WriteStartObject("exception");
        writer.WriteString("type", exception.GetType().FullName);
        writer.WriteString("message", exception.Message);
        writer.WriteNumber("hresult", exception.HResult);
        writer.WriteString("stackTrace", exception.StackTrace);
        writer.WriteEndObject();
    }

    private static bool IsForbiddenPropertyKey(string key)
    {
        foreach (string forbidden in ForbiddenPropertyKeys)
        {
            if (string.Equals(forbidden, key, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private void RotateIfNeeded(int incomingBytes)
    {
        if (!File.Exists(_path))
        {
            return;
        }

        long currentLength = new FileInfo(_path).Length;
        if (currentLength == 0 || currentLength + incomingBytes <= _maxFileBytes)
        {
            return;
        }

        if (_maxArchives == 0)
        {
            File.Delete(_path);
            return;
        }

        string oldest = ArchivePath(_maxArchives);
        if (File.Exists(oldest))
        {
            File.Delete(oldest);
        }

        for (int index = _maxArchives - 1; index >= 1; index--)
        {
            string source = ArchivePath(index);
            if (File.Exists(source))
            {
                File.Move(source, ArchivePath(index + 1), overwrite: true);
            }
        }

        File.Move(_path, ArchivePath(1), overwrite: true);
    }

    private string ArchivePath(int index) => Path.Combine(_directory, $"eft-toolkit.{index}.log");
}
