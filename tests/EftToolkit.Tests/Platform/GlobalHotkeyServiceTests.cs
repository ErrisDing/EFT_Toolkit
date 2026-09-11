using EftToolkit.Core.Display;
using EftToolkit.Platform.Windows.Hotkeys;
using EftToolkit.Platform.Windows.Messaging;

namespace EftToolkit.Tests.Platform;

public class GlobalHotkeyServiceTests
{
    private readonly FakeWindowsMessageSource _source = new();
    private readonly FakeHotkeyRegistrar _registrar = new();

    private GlobalHotkeyService CreateService() => new(_source, _registrar);

    [Fact]
    public async Task Register_binds_f2_through_f5_with_no_modifier_and_no_repeat()
    {
        GlobalHotkeyService service = CreateService();
        await service.RegisterAsync(CancellationToken.None);

        Assert.Equal(4, _registrar.Attempts.Count);

        foreach ((int hotkeyId, uint modifiers, uint virtualKey) in _registrar.Attempts)
        {
            Assert.Equal(WindowsMessageDecoder.ModNoRepeat, modifiers);
            Assert.Equal(WindowsMessageDecoder.VirtualKeyFor(WindowsMessageDecoder.DecodeHotkey(hotkeyId)), virtualKey);
        }

        Assert.True(_source.Started);
    }

    [Fact]
    public async Task Register_keeps_the_other_keys_when_one_is_already_taken()
    {
        // Another application owning F3 must not cost the user F2, F4, and F5.
        _registrar.Refused.Add(WindowsMessageDecoder.HotkeyIdLow);

        GlobalHotkeyService service = CreateService();
        await service.RegisterAsync(CancellationToken.None);

        Assert.False(service.Registrations[DisplayPresetKind.Low]);
        Assert.True(service.Registrations[DisplayPresetKind.Original]);
        Assert.True(service.Registrations[DisplayPresetKind.Medium]);
        Assert.True(service.Registrations[DisplayPresetKind.High]);
    }

    [Fact]
    public void Registrations_reports_every_preset_even_before_registering()
    {
        GlobalHotkeyService service = CreateService();

        Assert.False(service.Registrations[DisplayPresetKind.Original]);
        Assert.False(service.Registrations[DisplayPresetKind.Low]);
        Assert.False(service.Registrations[DisplayPresetKind.Medium]);
        Assert.False(service.Registrations[DisplayPresetKind.High]);
    }

    [Fact]
    public async Task Register_reports_all_four_presets_as_active_when_nothing_collides()
    {
        GlobalHotkeyService service = CreateService();
        await service.RegisterAsync(CancellationToken.None);

        Assert.All(service.Registrations.Values, Assert.True);
        Assert.Equal(4, service.Registrations.Count);
    }

    [Theory]
    [InlineData(DisplayPresetKind.Original)]
    [InlineData(DisplayPresetKind.Low)]
    [InlineData(DisplayPresetKind.Medium)]
    [InlineData(DisplayPresetKind.High)]
    public async Task PresetRequested_is_raised_for_a_registered_shortcut(DisplayPresetKind preset)
    {
        GlobalHotkeyService service = CreateService();
        await service.RegisterAsync(CancellationToken.None);

        List<DisplayPresetKind> raised = [];
        service.PresetRequested += (_, requested) => raised.Add(requested);

        _source.Deliver(WindowsMessageDecoder.WmHotkey, (nuint)WindowsMessageDecoder.HotkeyIdFor(preset));

        Assert.Equal([preset], raised);
    }

    [Fact]
    public async Task A_hotkey_another_application_owns_is_not_raised_even_if_the_message_arrives()
    {
        _registrar.Refused.Add(WindowsMessageDecoder.HotkeyIdHigh);

        GlobalHotkeyService service = CreateService();
        await service.RegisterAsync(CancellationToken.None);

        List<DisplayPresetKind> raised = [];
        service.PresetRequested += (_, requested) => raised.Add(requested);

        _source.Deliver(WindowsMessageDecoder.WmHotkey, (nuint)WindowsMessageDecoder.HotkeyIdHigh);

        Assert.Empty(raised);
    }

    [Theory]
    // An ID this toolkit never registered, and the other messages the window receives. None of them
    // is a preset request, and none of them may throw out of the message loop.
    [InlineData(0x0312u, 0xEF24u)]
    [InlineData(0x0312u, 0u)]
    [InlineData(0x007Eu, 0u)]
    [InlineData(0x0218u, 0x0012u)]
    public async Task An_unrelated_message_does_not_raise_a_preset(uint id, nuint wParam)
    {
        GlobalHotkeyService service = CreateService();
        await service.RegisterAsync(CancellationToken.None);

        List<DisplayPresetKind> raised = [];
        service.PresetRequested += (_, requested) => raised.Add(requested);

        _source.Deliver(id, wParam);

        Assert.Empty(raised);
    }

    [Fact]
    public async Task Unregister_releases_exactly_the_keys_that_were_registered()
    {
        _registrar.Refused.Add(WindowsMessageDecoder.HotkeyIdMedium);

        GlobalHotkeyService service = CreateService();
        await service.RegisterAsync(CancellationToken.None);
        await service.UnregisterAsync(CancellationToken.None);

        Assert.Equal(3, _registrar.UnregisterCount);
        Assert.Empty(_registrar.Registered);
        Assert.True(_source.Stopped);
    }

    [Fact]
    public async Task Unregister_releases_the_shortcuts_before_the_window_is_destroyed()
    {
        // RegisterHotKey binds the key to the window. Destroying the window first would leave the
        // registration attached to a handle that no longer exists.
        GlobalHotkeyService service = CreateService();
        await service.RegisterAsync(CancellationToken.None);
        await service.UnregisterAsync(CancellationToken.None);

        Assert.Contains("window.create", _source.Journal);
        Assert.Contains("window.destroy", _source.Journal);

        int lastUnregister = _source.Journal.FindLastIndex(entry => entry.StartsWith("hotkey.unregister", StringComparison.Ordinal));
        int destroy = _source.Journal.IndexOf("window.destroy");

        Assert.True(lastUnregister < destroy, $"expected every unregister before window.destroy, got [{string.Join(", ", _source.Journal)}]");
    }

    [Fact]
    public async Task Unregister_is_safe_without_a_prior_register()
    {
        GlobalHotkeyService service = CreateService();

        await service.UnregisterAsync(CancellationToken.None);
        await service.UnregisterAsync(CancellationToken.None);

        Assert.Equal(0, _registrar.UnregisterCount);
        Assert.All(service.Registrations.Values, Assert.False);
    }

    [Fact]
    public async Task Register_is_idempotent_and_does_not_take_the_keys_twice()
    {
        GlobalHotkeyService service = CreateService();
        await service.RegisterAsync(CancellationToken.None);
        await service.RegisterAsync(CancellationToken.None);

        Assert.Equal(4, _registrar.Attempts.Count);
    }

    [Fact]
    public async Task Dispose_releases_the_shortcuts()
    {
        GlobalHotkeyService service = CreateService();
        await service.RegisterAsync(CancellationToken.None);

        await service.DisposeAsync();

        Assert.Empty(_registrar.Registered);
    }

    [Fact]
    public async Task A_shortcut_stops_being_raised_after_unregistering()
    {
        GlobalHotkeyService service = CreateService();
        await service.RegisterAsync(CancellationToken.None);

        List<DisplayPresetKind> raised = [];
        service.PresetRequested += (_, requested) => raised.Add(requested);

        await service.UnregisterAsync(CancellationToken.None);

        // The window is gone, but a message already in flight must not resurrect a preset request.
        _source.Deliver(WindowsMessageDecoder.WmHotkey, (nuint)WindowsMessageDecoder.HotkeyIdHigh);

        Assert.Empty(raised);
    }
}
