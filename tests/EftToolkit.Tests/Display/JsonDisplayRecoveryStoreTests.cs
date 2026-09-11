using System.Text.Json;
using System.Text.Json.Nodes;
using EftToolkit.Display.Gamma;
using EftToolkit.Display.Recovery;

namespace EftToolkit.Tests.Display;

public class JsonDisplayRecoveryStoreTests : IDisposable
{
    private static readonly DateTimeOffset CapturedAt = new(2026, 9, 11, 10, 30, 0, TimeSpan.Zero);

    private readonly string _directory;
    private readonly JsonDisplayRecoveryStore _store;

    public JsonDisplayRecoveryStoreTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "eft-toolkit-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _store = new JsonDisplayRecoveryStore(_directory, TimeProvider.System);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temporary directory is not worth failing a test over.
        }
    }

    private string RecoveryPath => Path.Combine(_directory, JsonDisplayRecoveryStore.FileName);

    [Fact]
    public async Task LoadAsync_returns_null_when_nothing_has_been_saved()
    {
        Assert.Null(await _store.LoadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task SaveAsync_then_LoadAsync_returns_the_ramp_unchanged()
    {
        GammaRamp original = Ramp(200);
        await _store.SaveAsync(Snapshot(Entry("display-1", original)), CancellationToken.None);

        DisplayRecoverySnapshot? loaded = await _store.LoadAsync(CancellationToken.None);

        Assert.NotNull(loaded);
        Assert.Single(loaded!.Displays);
        Assert.True(loaded.Displays[0].TryReadOriginalRamp(out GammaRamp? ramp));
        Assert.Equal(original, ramp);
    }

    [Fact]
    public async Task SaveAsync_writes_atomically_and_leaves_no_temporary_file()
    {
        await _store.SaveAsync(Snapshot(Entry("display-1", Ramp(200))), CancellationToken.None);

        Assert.True(File.Exists(RecoveryPath));
        Assert.False(File.Exists(RecoveryPath + ".tmp"));
    }

    [Fact]
    public async Task LoadAsync_keeps_the_entries_that_are_intact_and_drops_the_tampered_one()
    {
        await _store.SaveAsync(
            Snapshot(Entry("display-1", Ramp(200)), Entry("display-2", Ramp(210))),
            CancellationToken.None);

        // Replace the first display's ramp without recomputing its checksum, which is what a
        // hand-edited file or a partially written one looks like. The edit goes through the JSON
        // DOM rather than a string replace, because the default encoder escapes "+" inside the
        // base64 payload and a literal replace would silently match nothing.
        JsonNode root = JsonNode.Parse(await File.ReadAllTextAsync(RecoveryPath))!;
        string before = root["displays"]![0]!["originalRampBase64"]!.GetValue<string>();
        root["displays"]![0]!["originalRampBase64"] = GammaRampCodec.ToBase64(Ramp(220));

        Assert.NotEqual(GammaRampCodec.ToBase64(Ramp(220)), before);
        await File.WriteAllTextAsync(RecoveryPath, root.ToJsonString());

        DisplayRecoverySnapshot? loaded = await _store.LoadAsync(CancellationToken.None);

        Assert.NotNull(loaded);
        Assert.Single(loaded!.Displays);
        Assert.Equal("display-2", loaded.Displays[0].StableId);
    }

    [Fact]
    public async Task LoadAsync_renames_a_file_that_is_not_json()
    {
        await File.WriteAllTextAsync(RecoveryPath, "{ this is not json");

        Assert.Null(await _store.LoadAsync(CancellationToken.None));

        Assert.False(File.Exists(RecoveryPath));
        Assert.Single(Directory.GetFiles(_directory, "display-recovery.corrupt.*.json"));
    }

    [Fact]
    public async Task LoadAsync_refuses_an_unsupported_schema_version()
    {
        await File.WriteAllTextAsync(
            RecoveryPath,
            JsonSerializer.Serialize(new DisplayRecoverySnapshot(99, [])));

        Assert.Null(await _store.LoadAsync(CancellationToken.None));
        Assert.Single(Directory.GetFiles(_directory, "display-recovery.corrupt.*.json"));
    }

    [Fact]
    public async Task RemoveAsync_drops_only_the_named_entries()
    {
        await _store.SaveAsync(
            Snapshot(Entry("display-1", Ramp(200)), Entry("display-2", Ramp(210))),
            CancellationToken.None);

        await _store.RemoveAsync(new HashSet<string>(["display-1"], StringComparer.Ordinal), CancellationToken.None);

        DisplayRecoverySnapshot? loaded = await _store.LoadAsync(CancellationToken.None);
        Assert.NotNull(loaded);
        Assert.Single(loaded!.Displays);
        Assert.Equal("display-2", loaded.Displays[0].StableId);
    }

    [Fact]
    public async Task RemoveAsync_deletes_the_file_once_nothing_is_left_to_recover()
    {
        await _store.SaveAsync(Snapshot(Entry("display-1", Ramp(200))), CancellationToken.None);

        await _store.RemoveAsync(new HashSet<string>(["display-1"], StringComparer.Ordinal), CancellationToken.None);

        Assert.False(File.Exists(RecoveryPath));
        Assert.Null(await _store.LoadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task RemoveAsync_ignores_ids_that_are_not_present()
    {
        await _store.SaveAsync(Snapshot(Entry("display-1", Ramp(200))), CancellationToken.None);

        await _store.RemoveAsync(new HashSet<string>(["display-9"], StringComparer.Ordinal), CancellationToken.None);

        DisplayRecoverySnapshot? loaded = await _store.LoadAsync(CancellationToken.None);
        Assert.NotNull(loaded);
        Assert.Single(loaded!.Displays);
    }

    [Fact]
    public async Task SaveAsync_with_no_entries_removes_the_file()
    {
        await _store.SaveAsync(Snapshot(Entry("display-1", Ramp(200))), CancellationToken.None);

        await _store.SaveAsync(DisplayRecoverySnapshot.Empty, CancellationToken.None);

        Assert.False(File.Exists(RecoveryPath));
    }

    [Fact]
    public async Task Concurrent_saves_do_not_collide_and_leave_one_intact_snapshot()
    {
        // The module persists from the display worker and from a topology refresh, so two saves can
        // be in flight at once. A shared temporary path would make the second one throw a sharing
        // violation instead of simply winning or losing the race.
        JsonDisplayRecoveryStore store = new(_directory, TimeProvider.System);

        Task[] saves = [.. Enumerable.Range(0, 32).Select(index => Task.Run(() =>
            store.SaveAsync(Snapshot(Entry($"display-{index}", Ramp(200))), CancellationToken.None)))];

        await Task.WhenAll(saves);

        DisplayRecoverySnapshot? loaded = await store.LoadAsync(CancellationToken.None);
        Assert.NotNull(loaded);
        Assert.Single(loaded!.Displays);
        Assert.True(loaded.Displays[0].TryReadOriginalRamp(out _));
    }

    [Fact]
    public void An_entry_whose_ramp_is_not_a_ramp_is_rejected()
    {
        DisplayRecoveryEntry entry = Entry("display-1", Ramp(200)) with { OriginalRampBase64 = "bm90IGEgcmFtcA==" };

        Assert.False(entry.TryReadOriginalRamp(out _));
    }

    private static DisplayRecoverySnapshot Snapshot(params DisplayRecoveryEntry[] entries) =>
        new(DisplayRecoverySnapshot.CurrentSchemaVersion, entries);

    private static DisplayRecoveryEntry Entry(string stableId, GammaRamp original) =>
        DisplayRecoveryEntry.Create(stableId, original, "ABCDEF", CapturedAt);

    /// <summary>A distinctly non-identity ramp, so a round-trip failure cannot hide behind equality.</summary>
    private static GammaRamp Ramp(ushort step)
    {
        ushort[] channel = Enumerable.Range(0, 256).Select(index => (ushort)(index * step)).ToArray();
        return new GammaRamp(channel, channel, channel);
    }
}
