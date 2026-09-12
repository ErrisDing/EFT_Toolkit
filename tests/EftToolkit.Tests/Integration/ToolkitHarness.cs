using EftToolkit.Audio;
using EftToolkit.Audio.Devices;
using EftToolkit.Core.Configuration;
using EftToolkit.Core.Display;
using EftToolkit.Core.Lifecycle;
using EftToolkit.Display;
using EftToolkit.Display.Gamma;
using EftToolkit.Display.Recovery;
using EftToolkit.Tests.Display;
using EftToolkit.Tests.TestSupport;

namespace EftToolkit.Tests.Integration;

/// <summary>
/// The whole toolkit, wired the way the composition root wires it, with only the drivers replaced.
/// </summary>
/// <remarks>
/// <para>
/// These tests exist for the seams between the two halves: what a fault in one is allowed to do to
/// the other, what a shutdown does before it disposes anything, and what a second launch makes of
/// what the previous one left behind. Every one of those is a question about the composition, so
/// stubbing a module here would be testing the stub.
/// </para>
/// <para>
/// One shared journal records the operations that have to happen in a particular order — a ramp
/// written, a shortcut released — because the order is the thing being asserted, and a log of
/// completed steps elsewhere only says what happened and not when.
/// </para>
/// </remarks>
internal sealed class ToolkitHarness : IAsyncDisposable
{
    internal const string FirstDisplayId = "display-1";
    internal const string SecondDisplayId = "display-2";
    internal const string VirtualRenderId = "virtual-render";
    internal const string VirtualCaptureId = "virtual-capture";
    internal const string PhysicalRenderId = "headphones";

    /// <summary>What is on each display before the toolkit has touched anything.</summary>
    internal static readonly GammaRamp FirstOriginal = BuildRamp(257);

    internal static readonly GammaRamp SecondOriginal = BuildRamp(250);

    private readonly string _directory;

    private ToolkitHarness(
        string directory,
        ToolkitOptions options,
        IReadOnlyList<(string StableId, GammaRamp Current)> displays)
    {
        _directory = directory;
        Options = options;
        Clock = new FixedTimeProvider(new DateTimeOffset(2026, 9, 11, 12, 0, 0, TimeSpan.Zero));

        Sessions = [];
        Store = new MemoryOptionsStore(options);
        Gateway = new FakeDisplayGammaGateway { Journal = Journal };

        foreach ((string stableId, GammaRamp current) in displays)
        {
            Gateway.AddDisplay(stableId, stableId, current);
        }

        Catalog = new StubDeviceCatalog { Endpoints = Endpoints() };
        Processes = new StubProcessMonitor { IsRunning = true };
        Hotkeys = new StubHotkeys(Journal);
        Events = new StubEvents(Journal);

        Display = new DisplayModule(
            options.Display,
            Gateway,
            new JsonDisplayRecoveryStore(directory, Clock),
            logger: null,
            Clock);

        Audio = new AudioModule(
            options.Audio,
            Catalog,
            Processes,
            CreateSession,
            Store,
            logger: null,
            Clock);

        Coordinator = new ToolkitCoordinator(
            Display, Audio, Hotkeys, Events, Store, logger: null, Clock);
    }

    /// <summary>What the runtime did, in the order it did it.</summary>
    internal List<string> Journal { get; } = [];

    internal ToolkitOptions Options { get; }

    internal FixedTimeProvider Clock { get; }

    internal MemoryOptionsStore Store { get; }

    internal FakeDisplayGammaGateway Gateway { get; }

    internal StubDeviceCatalog Catalog { get; }

    internal StubProcessMonitor Processes { get; }

    /// <summary>Holds no shortcut, and announces the release so its place in a shutdown is assertable.</summary>
    internal StubHotkeys Hotkeys { get; }

    internal StubEvents Events { get; }

    internal DisplayModule Display { get; }

    internal AudioModule Audio { get; }

    internal ToolkitCoordinator Coordinator { get; }

    /// <summary>Every stream the audio module has asked for, in order.</summary>
    internal List<StubStreamSession> Sessions { get; }

    /// <summary>The directory the recovery file is written to, kept so a test can read it back.</summary>
    internal string Directory => _directory;

    /// <summary>
    /// A machine with both halves configured and both switches on, started the way the composition
    /// root starts them.
    /// </summary>
    internal static async Task<ToolkitHarness> StartAsync(
        bool displayEnabled = true,
        bool audioEnabled = true,
        string[]? selectedDisplayIds = null,
        ToolkitOptions? options = null)
    {
        ToolkitHarness harness = Create(
            displayEnabled,
            audioEnabled,
            selectedDisplayIds,
            options);

        await harness.Coordinator.StartAsync(CancellationToken.None).ConfigureAwait(false);

        return harness;
    }

    /// <summary>
    /// Builds the toolkit without starting it, for a test that has to arrange the machine — a
    /// previous run's leftovers, say — before the first thing the runtime does with it.
    /// </summary>
    internal static ToolkitHarness Create(
        bool displayEnabled = true,
        bool audioEnabled = true,
        string[]? selectedDisplayIds = null,
        ToolkitOptions? options = null)
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "eft-toolkit-tests",
            Guid.NewGuid().ToString("N"));

        System.IO.Directory.CreateDirectory(directory);

        return CreateOver(
            directory,
            selectedDisplayIds,
            options,
            displayEnabled,
            audioEnabled,
            (FirstDisplayId, FirstOriginal),
            (SecondDisplayId, SecondOriginal));
    }

    /// <summary>
    /// Builds the toolkit over a directory that already exists, with the displays holding the ramps
    /// the caller names. That is how a second launch is reproduced: the same files, a different
    /// machine state.
    /// </summary>
    internal static ToolkitHarness CreateOver(
        string directory,
        string[]? selectedDisplayIds,
        ToolkitOptions? options,
        bool displayEnabled = true,
        bool audioEnabled = true,
        params (string StableId, GammaRamp Current)[] displays)
    {
        ToolkitOptions built = options ?? ToolkitOptions.CreateDefault() with
        {
            Display = new DisplayOptions(
                Enabled: displayEnabled,
                SelectedDisplayIds: selectedDisplayIds ?? [FirstDisplayId],
                Low: new DisplayPresetOptions(1.15, 0.00, 1.00),
                Medium: new DisplayPresetOptions(1.35, 0.01, 1.00),
                High: new DisplayPresetOptions(1.55, 0.02, 1.00)),
            Audio = ToolkitOptions.CreateDefault().Audio with
            {
                Enabled = audioEnabled,
                Profiles =
                [
                    Assert.Single(ToolkitOptions.CreateDefault().Audio.Profiles) with
                    {
                        VirtualRenderEndpointId = VirtualRenderId,
                        VirtualCaptureEndpointId = VirtualCaptureId,
                        PhysicalRenderEndpointId = PhysicalRenderId,
                    },
                ],
            },
        };

        return new ToolkitHarness(directory, built, displays);
    }

    /// <summary>Deletes the working directory of a run that was deliberately never shut down.</summary>
    internal static void Release(string directory)
    {
        try
        {
            System.IO.Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temporary directory is not worth failing a test over.
        }
    }

    /// <summary>
    /// Presses one of the display shortcuts and waits for the ramp to reach every display it should
    /// have reached.
    /// </summary>
    /// <remarks>
    /// The press goes through the hotkey service's event rather than straight to the display module,
    /// because that is the path a key press actually takes: the coordinator is what decides whether
    /// the press does anything at all, and a test that skipped it would not notice the day the
    /// decision stopped being made.
    /// </remarks>
    internal async Task PressPresetAsync(DisplayPresetKind preset, params string[] expectingWriteTo)
    {
        Hotkeys.Press(preset);

        foreach (string stableId in expectingWriteTo)
        {
            await WaitForWriteAsync(stableId).ConfigureAwait(false);
        }

        // A display write is only the first half of a completed preset operation. The worker then
        // persists the fingerprint it wrote so a later process can distinguish that ramp from one
        // written by another application. Waiting only for the gateway leaves the caller racing
        // that save: a test that starts a replacement harness immediately can read the older
        // fingerprint and correctly refuse to restore what now looks like an external change.
        Dictionary<string, string> expectedFingerprints = expectingWriteTo.ToDictionary(
            stableId => stableId,
            stableId => GammaRampFingerprint.Compute(Gateway.CurrentRamp(stableId)),
            StringComparer.Ordinal);

        await AsyncWait.UntilAsync(
            () => RecoveryContains(expectedFingerprints),
            "the recovery file records every ramp that was written").ConfigureAwait(false);
    }

    /// <summary>Waits until a ramp has reached a display.</summary>
    internal Task WaitForWriteAsync(string stableId) =>
        AsyncWait.UntilAsync(
            () => Gateway.WriteCountByDisplay.GetValueOrDefault(stableId) > 0,
            $"a ramp was written to {stableId}");

    /// <summary>Waits until a display holds a ramp with the given fingerprint.</summary>
    internal Task WaitForRampAsync(string stableId, GammaRamp expected) =>
        AsyncWait.UntilAsync(
            () => GammaRampFingerprint.Compute(Gateway.CurrentRamp(stableId))
                == GammaRampFingerprint.Compute(expected),
            $"{stableId} holds the ramp that was written");

    private bool RecoveryContains(IReadOnlyDictionary<string, string> expectedFingerprints)
    {
        DisplayRecoverySnapshot? snapshot = new JsonDisplayRecoveryStore(_directory, Clock)
            .LoadAsync(CancellationToken.None)
            .GetAwaiter()
            .GetResult();

        if (snapshot is null)
        {
            return false;
        }

        Dictionary<string, string> persisted = snapshot.Displays.ToDictionary(
            entry => entry.StableId,
            entry => entry.LastWrittenFingerprint,
            StringComparer.Ordinal);

        return expectedFingerprints.All(expected =>
            persisted.TryGetValue(expected.Key, out string? fingerprint)
            && string.Equals(fingerprint, expected.Value, StringComparison.Ordinal));
    }

    public async ValueTask DisposeAsync()
    {
        await Coordinator.ShutdownAsync(CancellationToken.None).ConfigureAwait(false);
        await Display.DisposeAsync().ConfigureAwait(false);
        await Audio.DisposeAsync().ConfigureAwait(false);
        await Catalog.DisposeAsync().ConfigureAwait(false);

        try
        {
            System.IO.Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temporary directory is not worth failing a test over.
        }
    }

    private StubStreamSession CreateSession()
    {
        StubStreamSession session = new();
        Sessions.Add(session);
        return session;
    }

    private static List<AudioEndpointDescriptor> Endpoints() =>
    [
        new(VirtualRenderId, "CABLE Input (VB-Audio Virtual Cable)", AudioDataFlow.Render, true, 2, 48_000),
        new(VirtualCaptureId, "CABLE Output (VB-Audio Virtual Cable)", AudioDataFlow.Capture, true, 2, 48_000),
        new(PhysicalRenderId, "Headphones", AudioDataFlow.Render, true, 2, 48_000),
    ];

    /// <summary>
    /// A ramp that climbs in fixed steps. Enough to tell one ramp from another; the tests here are
    /// about which ramp reached a display and when, not about what it looks like.
    /// </summary>
    private static GammaRamp BuildRamp(ushort step)
    {
        ushort[] channel = [.. Enumerable.Range(0, GammaRamp.ChannelLength).Select(index => (ushort)(index * step))];

        return new GammaRamp(channel, channel, channel);
    }
}
