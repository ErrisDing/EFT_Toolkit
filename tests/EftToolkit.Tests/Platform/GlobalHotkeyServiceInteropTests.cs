using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using EftToolkit.Platform.Windows.Hotkeys;
using EftToolkit.Platform.Windows.Interop;
using EftToolkit.Platform.Windows.Messaging;

namespace EftToolkit.Tests.Platform;

/// <summary>
/// Drives <see cref="GlobalHotkeyService"/> with the real message window and the real
/// <c>RegisterHotKey</c>, and asks Windows whether the toolkit still holds the keys.
/// </summary>
/// <remarks>
/// <para>
/// The substituted tests prove the service's logic; only Windows can answer whether a key is
/// actually bound to the toolkit's window after the shortcuts have been released and taken again.
/// That is the path the panel takes when the user switches the display enhancement off and on, and
/// the path on which the shortcuts used to be silently lost.
/// </para>
/// <para>
/// The question is asked from a <em>separate process</em>, which is not incidental.
/// <c>RegisterHotKey</c> keys a registration by the pair of window and identifier, and a second
/// registration made with no window at all is accepted even while a window in the same process holds
/// the key. Only a registration attempted from another process is refused with
/// <c>ERROR_HOTKEY_ALREADY_REGISTERED</c>, so a probe in this process would report every key free and
/// the test would assert nothing.
/// </para>
/// <para>
/// The keys are named rather than taken from <c>WindowsMessageDecoder</c> and the identifiers are
/// shifted, so a suite running alongside a copy of the application does not collide with it. A test
/// that cannot take the keys at all is skipped rather than failed: that means the keys are in use on
/// this machine, which says nothing about the code under test.
/// </para>
/// </remarks>
[Collection(WindowsMessageCollection.Name)]
public class GlobalHotkeyServiceInteropTests : IDisposable
{
    /// <summary>The keys the application's four presets are moved onto: F13-F16 in preset order.</summary>
    private const uint VirtualKeyF13 = 0x7C;
    private const uint VirtualKeyF14 = 0x7D;
    private const uint VirtualKeyF15 = 0x7E;
    private const uint VirtualKeyF16 = 0x7F;

    /// <summary>Far from the toolkit's <c>0xEF2x</c> range, so the two can never be confused in a log.</summary>
    private const int IdentifierOffset = 0x0100;

    private const string Held = "HELD";
    private const string Free = "FREE";
    private const string Unknown = "UNKNOWN";

    private static readonly uint[] VirtualKeys = [VirtualKeyF13, VirtualKeyF14, VirtualKeyF15, VirtualKeyF16];

    private readonly string _probeScript = Path.Combine(
        Path.GetTempPath(),
        $"eft-toolkit-hotkey-probe.{Guid.NewGuid():N}.ps1");

    public GlobalHotkeyServiceInteropTests() =>
        File.WriteAllText(
            _probeScript,
            """
            param([Parameter(Mandatory = $true)][string]$VirtualKeys)

            Add-Type -TypeDefinition @"
            using System;
            using System.Runtime.InteropServices;
            public static class EftToolkitHotkeyProbe
            {
                [DllImport("user32.dll", SetLastError = true)]
                public static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint virtualKey);
                [DllImport("user32.dll", SetLastError = true)]
                public static extern bool UnregisterHotKey(IntPtr hwnd, int id);
            }
            "@

            # No window and no identifier this process keeps: the registration exists only long enough
            # for Windows to say whether the key is spoken for.
            foreach ($key in $VirtualKeys.Split(',')) {
                $virtualKey = [Convert]::ToUInt32($key, 16)
                $taken = [EftToolkitHotkeyProbe]::RegisterHotKey([IntPtr]::Zero, 0, 0x4000, $virtualKey)

                if ($taken) {
                    [void][EftToolkitHotkeyProbe]::UnregisterHotKey([IntPtr]::Zero, 0)
                    "FREE $key"
                }
                else {
                    # Deliberately not $error: that is a PowerShell automatic variable, and assigning
                    # to it fails with a message about a read-only property rather than a Win32 code.
                    $win32Error = [Runtime.InteropServices.Marshal]::GetLastWin32Error()

                    if ($win32Error -eq 1409) {
                        "HELD $key"
                    }
                    else {
                        "UNKNOWN $key $win32Error"
                    }
                }
            }
            """);

    public void Dispose()
    {
        try
        {
            File.Delete(_probeScript);
        }
        catch (IOException)
        {
            // A leftover file in the temporary directory is not worth failing a passing test over.
        }
    }

    [Fact]
    public async Task A_shortcut_released_and_taken_again_is_bound_to_the_toolkits_window()
    {
        Dictionary<uint, string> before = Probe();

        Assert.SkipUnless(
            before.Values.All(state => state == Free),
            $"a function key this test needs is already in use on this machine ({Describe(before)})");

        await using WindowsMessageSink sink = new();

        // Started where the composition root starts it: before the hotkey service, which shares the
        // window rather than owning it.
        await sink.StartAsync(CancellationToken.None);

        GlobalHotkeyService service = new(sink, new OffsetHotkeyRegistrar(IdentifierOffset));

        await service.RegisterAsync(CancellationToken.None);

        Dictionary<uint, string> afterRegister = Probe();

        Assert.True(
            service.Registrations.Values.All(held => held),
            $"the service reports {string.Join(", ", service.Registrations.Select(pair => $"{pair.Key}={pair.Value}"))} "
            + $"while Windows reports {Describe(afterRegister)} for window 0x{sink.WindowHandle:X}");

        Assert.Equal(AllHeld, Describe(afterRegister));

        // The ordinary path through the panel: the display enhancement goes off and comes back on.
        // The service used to stop and dispose the message window on the way out, which left this
        // call with no window to register against - every preset failed and the user was left with no
        // shortcuts at all until the application was restarted.
        await service.UnregisterAsync(CancellationToken.None);

        // Asserted in the middle, and this is the assertion that catches the old defect. With the
        // window destroyed before the keys were released, the keys stayed bound to a window that no
        // longer existed: the states below would all still read HELD, and the final probe would
        // report HELD as well and let a broken release pass as a working one.
        Assert.Equal(AllInState(Free), Describe(Probe()));

        await service.RegisterAsync(CancellationToken.None);

        Dictionary<uint, string> afterToggle = Probe();

        // The window has to have survived the round trip for the line below to be meaningful: the
        // old defect left the service reporting four successful registrations while every one of
        // them had failed against a window that no longer existed.
        Assert.NotEqual(0, sink.WindowHandle);

        Assert.True(
            service.Registrations.Values.All(held => held),
            $"after the toggle the service reports {string.Join(", ", service.Registrations.Select(pair => $"{pair.Key}={pair.Value}"))}");

        Assert.Equal(AllHeld, Describe(afterToggle));
    }

    [Fact]
    public async Task Releasing_the_shortcuts_hands_the_keys_back_to_windows()
    {
        Assert.SkipUnless(
            Probe().Values.All(state => state == Free),
            "a function key this test needs is already in use on this machine");

        await using WindowsMessageSink sink = new();
        await sink.StartAsync(CancellationToken.None);

        GlobalHotkeyService service = new(sink, new OffsetHotkeyRegistrar(IdentifierOffset));

        await service.RegisterAsync(CancellationToken.None);
        await service.UnregisterAsync(CancellationToken.None);

        // Asserted from outside the process because that is the only vantage point with an answer.
        // A key left bound after the panel says the display enhancement is off is a key that keeps
        // firing at whatever the user is doing.
        Assert.Equal(AllInState(Free), Describe(Probe()));
    }

    [Fact]
    public async Task The_window_outlives_the_shortcuts()
    {
        await using WindowsMessageSink sink = new();
        await sink.StartAsync(CancellationToken.None);

        nint window = sink.WindowHandle;

        GlobalHotkeyService service = new(sink, new OffsetHotkeyRegistrar(IdentifierOffset));

        await service.RegisterAsync(CancellationToken.None);
        await service.UnregisterAsync(CancellationToken.None);

        // The window carries display-change and session notifications for the platform event source,
        // which has to keep receiving them while the display shortcuts are off.
        Assert.Equal(window, sink.WindowHandle);

        TaskCompletionSource<WindowsMessage> received = new(TaskCreationOptions.RunContinuationsAsynchronously);
        sink.MessageReceived += (_, message) => received.TrySetResult(message);

        Assert.True(NativeMethods.PostMessage(window, 0x8000, 3, 4));

        WindowsMessage message = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal((nuint)3, message.WParam);
    }

    private static string AllHeld => AllInState(Held);

    private static string AllInState(string state) =>
        string.Join(", ", VirtualKeys.Select(key => $"{key:X2}={state}"));

    private static string Describe(IReadOnlyDictionary<uint, string> states) =>
        string.Join(", ", states.Select(pair => $"{pair.Key:X2}={pair.Value}"));

    private Dictionary<uint, string> Probe()
    {
        string arguments = string.Join(",", VirtualKeys.Select(key => key.ToString("X", CultureInfo.InvariantCulture)));

        ProcessStartInfo startInfo = new("powershell.exe")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(_probeScript);
        startInfo.ArgumentList.Add("-VirtualKeys");
        startInfo.ArgumentList.Add(arguments);

        using Process probe = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The hotkey probe process could not be started.");

        string output = probe.StandardOutput.ReadToEnd();
        string errors = probe.StandardError.ReadToEnd();

        probe.WaitForExit(milliseconds: 30_000);

        Dictionary<uint, string> states = [];

        foreach (string line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);

            if (parts.Length >= 2 && uint.TryParse(parts[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint key))
            {
                states[key] = parts[0];
            }
        }

        if (states.Count != VirtualKeys.Length)
        {
            throw new InvalidOperationException(
                $"The hotkey probe reported {states.Count} of {VirtualKeys.Length} keys. "
                + $"Output: <{output}>. Errors: <{errors}>.");
        }

        return states;
    }

    /// <summary>
    /// The real registrar, moved off the keys the application uses.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Both halves of the registration are moved: the virtual keys from F2-F5 onto F13-F16, so the
    /// test never takes a shortcut away from the machine it runs on, and the identifiers, so a copy
    /// of the toolkit bound to the same keys is not disturbed either. Nothing in the service is aware
    /// of this, which is the point: the code under test is the production one.
    /// </para>
    /// <para>
    /// The move is a lookup rather than an offset on the virtual key, because a virtual key is a
    /// named code and not a number: <c>0x71 + 0x100</c> is accepted by Windows as a key that does not
    /// exist, so an offset would bind a key the probe could never look for and the test would pass by
    /// measuring nothing.
    /// </para>
    /// </remarks>
    private sealed class OffsetHotkeyRegistrar(int offset) : IHotkeyRegistrar
    {
        private readonly Win32HotkeyRegistrar _inner = new();

        public bool TryRegister(nint windowHandle, int hotkeyId, uint modifiers, uint virtualKey) =>
            _inner.TryRegister(windowHandle, hotkeyId + offset, modifiers, Move(virtualKey));

        public void Unregister(nint windowHandle, int hotkeyId) =>
            _inner.Unregister(windowHandle, hotkeyId + offset);

        private static uint Move(uint virtualKey) => virtualKey switch
        {
            0x71 => VirtualKeyF13,
            0x72 => VirtualKeyF14,
            0x73 => VirtualKeyF15,
            0x74 => VirtualKeyF16,
            _ => throw new ArgumentOutOfRangeException(
                nameof(virtualKey),
                virtualKey,
                "The test only knows how to move the four function keys the presets use."),
        };
    }
}
