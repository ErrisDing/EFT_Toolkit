using System.Text.Json;
using EftToolkit.Core.Diagnostics;

namespace EftToolkit.Tests.Core;

public sealed class JsonLineLoggerTests : IDisposable
{
    private readonly string _directory;
    private readonly string _logPath;

    public JsonLineLoggerTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "eft-toolkit-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(_directory);
        _logPath = Path.Combine(_directory, "eft-toolkit.log");
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public async Task Write_emits_one_json_object_per_line()
    {
        await using (var logger = CreateLogger())
        {
            logger.Write(LogLevel.Information, "first");
            logger.Write(LogLevel.Warning, "second");
        }

        string[] lines = await File.ReadAllLinesAsync(_logPath);
        Assert.Equal(2, lines.Length);
        Assert.All(lines, line => Assert.StartsWith("{", line, StringComparison.Ordinal));
        Assert.Equal("first", JsonDocument.Parse(lines[0]).RootElement.GetProperty("event").GetString());
        Assert.Equal("second", JsonDocument.Parse(lines[1]).RootElement.GetProperty("event").GetString());
    }

    [Fact]
    public async Task Write_records_utc_timestamp_level_and_event_name()
    {
        var timestamp = new DateTimeOffset(2026, 9, 11, 13, 14, 15, TimeSpan.Zero);

        await using (var logger = CreateLogger(new FixedTimeProvider(timestamp)))
        {
            logger.Write(LogLevel.Critical, "audio.faulted");
        }

        using JsonDocument document = JsonDocument.Parse(await File.ReadAllTextAsync(_logPath));
        JsonElement root = document.RootElement;

        Assert.Equal("Critical", root.GetProperty("level").GetString());
        Assert.Equal("audio.faulted", root.GetProperty("event").GetString());
        Assert.Equal(timestamp, root.GetProperty("timestamp").GetDateTimeOffset());
        Assert.Equal(TimeSpan.Zero, root.GetProperty("timestamp").GetDateTimeOffset().Offset);
    }

    [Fact]
    public async Task Write_includes_properties()
    {
        await using (var logger = CreateLogger())
        {
            logger.Write(
                LogLevel.Information,
                "display.written",
                new Dictionary<string, object?>
                {
                    ["stableId"] = @"\\?\DISPLAY#ABC#1",
                    ["win32Error"] = 0,
                    ["matched"] = true,
                });
        }

        using JsonDocument document = JsonDocument.Parse(await File.ReadAllTextAsync(_logPath));
        JsonElement properties = document.RootElement.GetProperty("properties");

        Assert.Equal(@"\\?\DISPLAY#ABC#1", properties.GetProperty("stableId").GetString());
        Assert.Equal(0, properties.GetProperty("win32Error").GetInt32());
        Assert.True(properties.GetProperty("matched").GetBoolean());
    }

    [Fact]
    public async Task Write_serializes_exception_type_message_hresult_and_stack_trace()
    {
        Exception thrown;
        try
        {
            throw new InvalidOperationException("device lost");
        }
        catch (InvalidOperationException exception)
        {
            thrown = exception;

            await using var logger = CreateLogger();
            logger.Write(LogLevel.Error, "stream.faulted", exception: exception);
        }

        using JsonDocument document = JsonDocument.Parse(await File.ReadAllTextAsync(_logPath));
        JsonElement error = document.RootElement.GetProperty("exception");

        Assert.Equal(typeof(InvalidOperationException).FullName, error.GetProperty("type").GetString());
        Assert.Equal("device lost", error.GetProperty("message").GetString());
        Assert.Equal(thrown.HResult, error.GetProperty("hresult").GetInt32());
        Assert.False(string.IsNullOrWhiteSpace(error.GetProperty("stackTrace").GetString()));
    }

    [Theory]
    [InlineData("audioSamples")]
    [InlineData("AUDIOSAMPLES")]
    [InlineData("pcm")]
    [InlineData("Pcm")]
    [InlineData("bufferBytes")]
    [InlineData("BUFFERBYTES")]
    public async Task Write_redacts_captured_audio_property_keys_ignoring_case(string forbiddenKey)
    {
        await using (var logger = CreateLogger())
        {
            logger.Write(
                LogLevel.Debug,
                "stream.block",
                new Dictionary<string, object?>
                {
                    [forbiddenKey] = new byte[] { 1, 2, 3 },
                    ["frames"] = 480,
                });
        }

        using JsonDocument document = JsonDocument.Parse(await File.ReadAllTextAsync(_logPath));
        JsonElement properties = document.RootElement.GetProperty("properties");

        Assert.False(properties.TryGetProperty(forbiddenKey, out _));
        Assert.Equal(480, properties.GetProperty("frames").GetInt32());
        Assert.DoesNotContain("AQID", await File.ReadAllTextAsync(_logPath), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Write_rolls_the_log_and_keeps_only_the_configured_archive_count()
    {
        await using (var logger = new JsonLineLogger(_directory, TimeProvider.System, maxFileBytes: 256, maxArchives: 5))
        {
            for (int index = 0; index < 60; index++)
            {
                logger.Write(LogLevel.Information, "filler-" + index);
            }
        }

        Assert.True(File.Exists(_logPath));
        for (int archive = 1; archive <= 5; archive++)
        {
            Assert.True(File.Exists(ArchivePath(archive)), $"expected archive {archive} to exist");
        }

        Assert.False(File.Exists(ArchivePath(6)));
    }

    [Fact]
    public async Task Write_never_leaves_the_live_log_above_the_rotation_threshold()
    {
        await using (var logger = new JsonLineLogger(_directory, TimeProvider.System, maxFileBytes: 512, maxArchives: 3))
        {
            for (int index = 0; index < 40; index++)
            {
                logger.Write(LogLevel.Information, "filler-" + index);
            }
        }

        foreach (string file in Directory.GetFiles(_directory))
        {
            Assert.True(new FileInfo(file).Length <= 512, $"{Path.GetFileName(file)} exceeded the rotation threshold");
        }
    }

    [Fact]
    public void Defaults_are_five_mebibytes_and_five_archives()
    {
        Assert.Equal(5 * 1024 * 1024, JsonLineLogger.DefaultMaxFileBytes);
        Assert.Equal(5, JsonLineLogger.DefaultMaxArchives);
    }

    [Fact]
    public async Task Write_creates_the_log_directory_when_missing()
    {
        string nested = Path.Combine(_directory, "logs");

        await using (var logger = new JsonLineLogger(nested, TimeProvider.System))
        {
            logger.Write(LogLevel.Information, "created");
        }

        Assert.True(File.Exists(Path.Combine(nested, "eft-toolkit.log")));
    }

    [Fact]
    public async Task Write_escapes_multiline_messages_onto_a_single_line()
    {
        await using (var logger = CreateLogger())
        {
            logger.Write(LogLevel.Error, "multi", new Dictionary<string, object?> { ["detail"] = "line1\nline2" });
        }

        string[] lines = await File.ReadAllLinesAsync(_logPath);

        Assert.Single(lines);
        Assert.Equal("line1\nline2", JsonDocument.Parse(lines[0]).RootElement.GetProperty("properties").GetProperty("detail").GetString());
    }

    private static string ArchivePath(string directory, int index) => Path.Combine(directory, $"eft-toolkit.{index}.log");

    private string ArchivePath(int index) => ArchivePath(_directory, index);

    private JsonLineLogger CreateLogger(TimeProvider? timeProvider = null) =>
        new(_directory, timeProvider ?? TimeProvider.System);
}
