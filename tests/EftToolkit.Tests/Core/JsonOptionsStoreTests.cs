using EftToolkit.Core.Configuration;

namespace EftToolkit.Tests.Core;

public sealed class JsonOptionsStoreTests : IDisposable
{
    private readonly string _directory;
    private readonly string _settingsPath;

    public JsonOptionsStoreTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "eft-toolkit-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(_directory);
        _settingsPath = Path.Combine(_directory, "settings.json");
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public async Task LoadAsync_preserves_corrupt_file_and_returns_defaults()
    {
        await File.WriteAllTextAsync(_settingsPath, "{not-json");
        var store = new JsonOptionsStore(_directory, TimeProvider.System);

        ToolkitOptions actual = await store.LoadAsync(CancellationToken.None);

        Assert.Equal(ToolkitOptions.CreateDefault(), actual);
        Assert.Single(Directory.GetFiles(_directory, "settings.corrupt.*.json"));
    }

    [Fact]
    public async Task LoadAsync_never_deletes_the_corrupt_file()
    {
        const string garbage = "{not-json";
        await File.WriteAllTextAsync(_settingsPath, garbage);
        var store = new JsonOptionsStore(_directory, TimeProvider.System);

        await store.LoadAsync(CancellationToken.None);

        string preserved = Directory.GetFiles(_directory, "settings.corrupt.*.json").Single();
        Assert.Equal(garbage, await File.ReadAllTextAsync(preserved));
    }

    [Fact]
    public async Task SaveAsync_replaces_settings_atomically_and_leaves_no_temp_file()
    {
        var store = new JsonOptionsStore(_directory, TimeProvider.System);
        await store.SaveAsync(ToolkitOptions.CreateDefault(), CancellationToken.None);

        Assert.True(File.Exists(_settingsPath));
        Assert.False(File.Exists(_settingsPath + ".tmp"));
    }

    [Fact]
    public async Task SaveAsync_overwriting_existing_settings_leaves_no_temp_file()
    {
        var store = new JsonOptionsStore(_directory, TimeProvider.System);
        await store.SaveAsync(ToolkitOptions.CreateDefault(), CancellationToken.None);
        await store.SaveAsync(ToolkitOptions.CreateDefault() with { Display = ToolkitOptions.CreateDefault().Display with { Enabled = true } }, CancellationToken.None);

        Assert.False(File.Exists(_settingsPath + ".tmp"));
        Assert.True((await store.LoadAsync(CancellationToken.None)).Display.Enabled);
    }

    [Fact]
    public async Task LoadAsync_returns_defaults_when_no_file_exists()
    {
        var store = new JsonOptionsStore(_directory, TimeProvider.System);

        Assert.Equal(ToolkitOptions.CreateDefault(), await store.LoadAsync(CancellationToken.None));
        Assert.Empty(Directory.GetFiles(_directory));
    }

    [Fact]
    public async Task LoadAsync_round_trips_saved_options()
    {
        ToolkitOptions defaults = ToolkitOptions.CreateDefault();
        ToolkitOptions configured = defaults with
        {
            Display = defaults.Display with
            {
                Enabled = true,
                SelectedDisplayIds = ["path-a", "path-b"],
                High = new DisplayPresetOptions(2.25, 0.15, 0.80),
            },
            Audio = defaults.Audio with
            {
                Enabled = true,
                ActiveProfileId = "game",
                Profiles =
                [
                    .. defaults.Audio.Profiles,
                    new AudioProfileOptions("game", "Another Game", "AnotherGame", "vr", "vc", "pr"),
                ],
            },
        };

        var store = new JsonOptionsStore(_directory, TimeProvider.System);
        await store.SaveAsync(configured, CancellationToken.None);

        Assert.Equal(configured, await store.LoadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task SaveAsync_writes_camel_case_properties()
    {
        var store = new JsonOptionsStore(_directory, TimeProvider.System);
        await store.SaveAsync(ToolkitOptions.CreateDefault(), CancellationToken.None);

        string json = await File.ReadAllTextAsync(_settingsPath);

        Assert.Contains("\"schemaVersion\"", json, StringComparison.Ordinal);
        Assert.Contains("\"shadowLift\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"SchemaVersion\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LoadAsync_treats_unsupported_schema_version_as_corrupt()
    {
        await File.WriteAllTextAsync(
            _settingsPath,
            $$"""{"schemaVersion":{{ToolkitOptions.CurrentSchemaVersion + 1}},"display":null,"audio":null}""");
        var store = new JsonOptionsStore(_directory, TimeProvider.System);

        Assert.Equal(ToolkitOptions.CreateDefault(), await store.LoadAsync(CancellationToken.None));
        Assert.Single(Directory.GetFiles(_directory, "settings.corrupt.*.json"));
    }

    [Fact]
    public async Task LoadAsync_replaces_out_of_range_values_with_defaults_without_discarding_the_file()
    {
        await File.WriteAllTextAsync(
            _settingsPath,
            """
            {
              "schemaVersion": 1,
              "display": {
                "enabled": true,
                "selectedDisplayIds": ["keep-me"],
                "low": { "gamma": 99.0, "shadowLift": 0.00, "outputCeiling": 1.00 },
                "medium": { "gamma": 1.35, "shadowLift": 0.01, "outputCeiling": 1.00 },
                "high": { "gamma": 1.55, "shadowLift": 0.02, "outputCeiling": 1.00 }
              },
              "audio": null
            }
            """);
        var store = new JsonOptionsStore(_directory, TimeProvider.System);

        ToolkitOptions actual = await store.LoadAsync(CancellationToken.None);

        Assert.Equal(ToolkitOptions.CreateDefault().Display.Low.Gamma, actual.Display.Low.Gamma);
        Assert.True(actual.Display.Enabled);
        Assert.Equal(["keep-me"], actual.Display.SelectedDisplayIds);
        Assert.Empty(Directory.GetFiles(_directory, "settings.corrupt.*.json"));
    }

    [Fact]
    public async Task LoadAsync_names_the_corrupt_file_from_the_injected_clock()
    {
        var timestamp = new DateTimeOffset(2026, 9, 11, 13, 14, 15, 123, TimeSpan.Zero);
        await File.WriteAllTextAsync(_settingsPath, "{not-json");
        var store = new JsonOptionsStore(_directory, new FixedTimeProvider(timestamp));

        await store.LoadAsync(CancellationToken.None);

        Assert.True(File.Exists(Path.Combine(_directory, "settings.corrupt.20260911-131415123.json")));
    }

    [Fact]
    public void Default_root_is_the_local_application_data_directory()
    {
        Assert.Equal(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EftToolkit"),
            JsonOptionsStore.DefaultRoot);
    }

    [Fact]
    public async Task LoadAsync_reports_honoured_cancellation()
    {
        var store = new JsonOptionsStore(_directory, TimeProvider.System);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.LoadAsync(cancelled.Token));
    }
}
