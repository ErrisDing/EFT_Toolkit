# EFT Toolkit Design

Date: 2026-09-11

## Purpose

EFT Toolkit is a Windows 11 utility for two pain points in Escape from Tarkov:

1. Improve visibility in dark and indoor scenes by applying selectable display gamma ramps.
2. Increase the volume of a selected application while attenuating peaks that would otherwise clip after amplification.

The application must not inject into, hook, or modify EFT. Display changes are global to selected monitors. Audio uses Windows shared-mode APIs and must not open or reserve ASIO devices.

## Scope

The first release supports Windows 11 x64 only. It provides:

- A simple WPF panel and notification-area icon.
- Independently enabled display and audio modules.
- F2 through F5 global shortcuts for display presets.
- Multi-select display targeting.
- A virtual-audio-device-based processing path.
- Configurable audio gain and stereo-linked peak attenuation.
- Configuration, diagnostics, and recovery behavior.

The first release does not provide:

- Game injection, graphics API hooks, overlays, or process memory access.
- A custom APO, virtual audio driver, or bundled third-party driver.
- ASIO capture, playback, enumeration, or configuration.
- Automatic modification of Windows per-application output routing.
- Equalization, noise reduction, multiband compression, or true-peak metering.
- Automatic startup with Windows.

## Technology and Project Structure

The application uses C# on .NET 8 with WPF. Native Windows features are accessed through focused interop wrappers. NAudio provides WASAPI device and stream integration; the DSP remains application-owned and independently testable.

The solution is divided into these projects:

- `EftToolkit.App`: WPF panel, notification-area integration, view models, and user interaction.
- `EftToolkit.Core`: module orchestration, lifecycle, configuration contracts, and logging contracts.
- `EftToolkit.Display`: monitor discovery, gamma-ramp snapshots, preset generation, application, verification, and recovery.
- `EftToolkit.Audio`: dependency discovery, process monitoring, shared-mode capture and render streams, gain, and limiting.
- `EftToolkit.Platform.Windows`: global hotkeys and display, power, session, and native lifecycle events.
- `EftToolkit.Tests`: unit and component tests using fake platform boundaries.

Display and audio depend on abstractions owned by Core. Neither module depends on the other. A fault in one module must not disable or terminate the other.

## Application Lifecycle

Only one application instance may run. Starting a second instance activates the existing panel and exits the new process.

Each module follows an idempotent state model:

`Disabled -> Starting -> Active/Bypass -> Degraded/Faulted -> Stopping -> Disabled`

Repeated enable, disable, and stop requests are safe. A lifecycle coordinator owns shutdown ordering:

1. Reject new state-changing commands.
2. Put audio into bypass and stop its streams.
3. Restore display gamma ramps.
4. Unregister global hotkeys and native event subscriptions.
5. Persist final state and flush logs.

Closing the WPF window hides it to the notification area. It does not stop either module. Only the notification-area Exit command starts full shutdown. If the target process is still using the virtual audio endpoint, Exit requires confirmation that audio will become unavailable until the application is routed back to a physical endpoint.

An unhandled-exception handler attempts the same cleanup, but crash recovery does not assume cleanup succeeded. Recovery snapshots provide the display-side fallback on the next launch.

The application runs without administrator privileges. A feature that unexpectedly requires elevation fails locally and reports the problem instead of relaunching the entire application as administrator.

## Panel Design

The application has one compact window with two independent sections.

The header shows overall health and explains that the window close button minimizes to the notification area. The display section contains its enable switch, a multi-select monitor list, F2-F5 preset status, preset parameters, and per-monitor results. The audio section contains its enable switch, target application, virtual capture endpoint, physical render endpoint, gain and limiter settings, input/output meters, and current gain reduction.

The footer shows the most recent warning and a command to open the log directory. Expected operational errors appear inline and do not produce repeated modal dialogs.

## Display Module

### Monitor identity and selection

The module enumerates connected displays and maps Windows display topology identities to the GDI device contexts needed by the gamma-ramp API. A persisted selection uses a stable display target path where available, with adapter and display names retained for diagnostics. Users may select any number of compatible displays.

Each row reports:

- Connected or disconnected state.
- SDR or HDR state.
- Gamma-ramp API availability.
- Selected preset.
- Last write and readback result.

HDR is not blocked. The UI labels HDR operation experimental because Microsoft documents `SetDeviceGammaRamp` behavior in HDR as undefined. A successful readback means only that the driver exposed matching ramp data; it does not prove that the final displayed image used that data.

### Original ramp and presets

When the display module is enabled, it reads and retains each selected display's current 3-by-256 16-bit RGB ramp. It also writes a checksummed recovery snapshot containing the display identity, original ramp, timestamp, and the fingerprint of the last ramp written by the toolkit.

Shortcut behavior is fixed:

- F2: restore the captured original ramp and disable enhancement.
- F3: low enhancement.
- F4: medium enhancement.
- F5: high enhancement.

Low, medium, and high presets are configurable using a deliberately small parameter set: gamma, shadow lift, and output ceiling. For normalized input `x`, the initial transform is `t = shadowLift + (1 - shadowLift) * pow(x, 1 / gamma)`. The result is clamped to the configured ceiling and used to interpolate the captured original ramp. This composes the enhancement with the existing calibration instead of replacing it with an identity-based ramp. Generated channels must remain monotonic and must be clamped to the 16-bit output range.

The initial defaults are deliberately conservative:

- F3: gamma 1.15, shadow lift 0.00, output ceiling 1.00.
- F4: gamma 1.35, shadow lift 0.01, output ceiling 1.00.
- F5: gamma 1.55, shadow lift 0.02, output ceiling 1.00.

The panel validates gamma from 0.50 through 3.00, shadow lift from 0.00 through 0.20, and output ceiling from 0.50 through 1.00. Invalid persisted values fall back to the corresponding default.

### Applying and verifying ramps

One shortcut changes the logical current preset for all selected displays. Writes run serially on a background worker because a single `SetDeviceGammaRamp` call can take significant time. Repeated shortcut requests are coalesced so that the worker converges on the most recently requested preset without freezing the panel.

After each write, the module reads the ramp back and compares it within the exact representation returned by the driver. Write and verification status is tracked per monitor. Failure on one display does not roll back or block other displays.

The module listens for display topology, resolution, power resume, and session unlock events. It re-enumerates identities and reapplies the logical current preset to matching selected displays. It does not continuously fight other color-management software by polling and rewriting ramps.

### Hotkeys

F2 through F5 are registered with `RegisterHotKey`. The application does not install keyboard hooks. Disabling the display module or exiting unregisters all shortcuts. A registration collision is reported per key so remaining shortcuts can continue to work.

### Recovery and coexistence

A clean shutdown restores each captured original ramp. On startup after an unclean shutdown, the application restores a saved original ramp only when all of the following are true:

- The stable display identity matches.
- The current ramp fingerprint matches the last ramp written by this toolkit.
- The recovery snapshot passes its checksum.

If another application or the operating system changed the ramp, the toolkit preserves the current state and warns the user instead of applying stale calibration data. This is necessary because Windows or another application may overwrite a global gamma ramp, and display events often reset it.

## Audio Module

### Routing and dependency model

The recommended backend uses a separately installed virtual audio device such as VB-CABLE. The toolkit does not bundle, install, update, or license the third-party driver.

When the audio module is disabled, no dependency check or audio device access occurs. On enable, the module:

1. Confirms the configured virtual capture endpoint exists.
2. Confirms the selected physical output endpoint exists and supports WASAPI shared mode.
3. Starts monitoring the configured target executable.
4. Warns if unrelated active sessions appear on the virtual endpoint.
5. Starts processing when the target and required endpoints are available.

The user routes EFT, or another configured application, to the virtual endpoint using Windows settings. The toolkit does not automatically change application routing. It can store profiles for multiple executables, but only one target profile is active at a time. EFT is supplied as the default profile.

Isolation is provided by routing: every stream sent to the virtual endpoint is processed. Process selection controls monitoring, status, and warnings; it cannot prevent an incorrectly routed second application from entering the same virtual stream.

### ASIO coexistence

The toolkit uses WASAPI shared mode for both capture and render. It does not initialize, enumerate, configure, or hold ASIO devices or interfaces. Failure to open a selected endpoint in shared mode faults only the audio module. The toolkit never falls back to exclusive mode.

### Stream pipeline

The audio path is:

`virtual capture endpoint -> format conversion -> gain -> stereo-linked limiter -> sample ceiling -> physical render endpoint`

The primary processing format is stereo 32-bit floating point at 44.1 or 48 kHz. Windows shared-mode conversion handles endpoint format differences. The initial release does not claim surround processing support.

Gain is expressed in decibels. The limiter detects the larger absolute magnitude across left and right channels and applies one gain-reduction envelope to both channels. This preserves inter-channel balance and spatial cues. It uses a configurable threshold, ratio, soft knee, look-ahead, attack, and release. A final sample ceiling prevents newly amplified samples from exceeding the configured digital limit.

Initial audio defaults are +12 dB gain, -12 dBFS threshold, 10:1 ratio, 6 dB knee, 5 ms look-ahead, 1 ms attack, 100 ms release, and a -1 dBFS sample ceiling. The simple panel exposes gain, threshold, and ceiling. The other parameters remain versioned advanced configuration for the first release. Gain is restricted to 0 through +24 dB, and invalid or internally inconsistent limiter settings revert to defaults.

The limiter prevents digital clipping introduced by this tool; it is not a hearing-protection device and cannot guarantee safe acoustic pressure at the user's headphones.

The render path is event driven and bounded. It records underruns, overruns, stream restarts, and effective buffer settings. Device loss moves the module to Degraded or Faulted, releases stale clients, and retries only when a relevant device notification arrives or the user requests retry. It must not spin in a rapid retry loop.

On planned stop, gain ramps down briefly before streams close to avoid a discontinuity. The UI clearly distinguishes Active processing from Bypass, Waiting for target, Missing dependency, and Faulted states.

## Configuration and Diagnostics

Configuration is versioned JSON stored in the user's application-data directory. It includes module enablement, selected monitor identities, display presets, audio profiles, endpoint identities, gain, and limiter values. F2 through F5 are fixed and are not configurable in the first release. Writes use an atomic temporary-file replacement strategy.

Recovery snapshots are separate from normal preferences so a corrupt preference file cannot destroy display restoration data. A corrupt configuration is retained for diagnosis and replaced in memory with safe defaults.

Structured rolling logs include lifecycle transitions, device identities, Windows error codes, gamma-ramp fingerprints, stream formats, buffer metrics, and audio recovery events. Logs never contain captured audio samples.

## Error Handling

Errors are contained at the narrowest boundary:

- A display write failure changes only that display's status.
- A hotkey collision changes only that shortcut's status.
- A missing virtual audio dependency faults only the audio module.
- Capture or render device loss stops and releases only the audio stream.
- A malformed profile uses safe defaults and reports the affected field.

Native resources use deterministic disposal. Native callbacks marshal state changes onto owned workers rather than mutating UI or module state concurrently. Shutdown has a finite timeout and reports any resource that could not be released cleanly.

## Testing

### Automated tests

- Display curves are monotonic, in range, and free of integer overflow.
- F2 restoration reproduces the captured ramp exactly.
- Per-display failures do not corrupt another display's state.
- Audio gain matches configured decibel values.
- Both stereo channels receive identical limiter gain reduction.
- Output samples stay at or below the configured ceiling and never become NaN or infinity.
- Audio start, stop, device loss, and device recovery leave no owned stream active.
- Corrupt configuration loads safe defaults while retaining the original file.
- Repeated enable, disable, and shutdown calls remain idempotent.
- Lifecycle tests verify cleanup ordering with fake platform adapters.

### Windows 11 integration tests

- Switch F2-F5 on single- and multi-monitor systems.
- Disconnect and reconnect monitors, change resolution, lock and unlock, and suspend and resume.
- Record write and readback behavior in both SDR and HDR without blocking HDR.
- Exercise shortcuts while EFT is full screen.
- Start with a missing virtual endpoint and disconnect endpoints during streaming.
- Change the Windows default render endpoint while processing.
- Confirm unrelated applications bypass processing unless explicitly routed to the virtual endpoint.
- Run an ASIO client concurrently and confirm the toolkit does not access or interfere with it.
- Run audio for at least 30 minutes while observing glitches, memory growth, and handle growth.
- Close the panel and confirm tray operation keeps both active modules intact.
- Exit from the tray and confirm ramp restoration, hotkey unregistration, and audio resource release.

## Acceptance Criteria

- The packaged application runs on Windows 11 x64 without administrator privileges.
- The application contains no EFT injection, hook, overlay, or game-memory modification.
- F2 restores the original ramp; F3, F4, and F5 apply the configured levels to all selected monitors.
- A failed display does not prevent successful selected displays from changing.
- Audio activation is optional and missing dependencies do not affect display controls.
- The audio backend uses only WASAPI shared mode and never opens ASIO or exclusive-mode streams.
- Amplified output does not exceed the configured sample ceiling in automated DSP tests.
- Under a reference 48 kHz shared-mode setup, the target additional audio latency is no more than 50 ms. The panel reports when actual device buffer configuration cannot meet that target.
- Window close leaves active processing intact in the notification area.
- A clean application exit restores captured display ramps and releases all native audio and hotkey resources.

## Platform Constraints and References

Windows documents important limitations that remain visible in the product rather than being hidden:

- `SetDeviceGammaRamp` can silently decline ramps, can be overwritten by Windows or other applications, is reset by many display events, has undefined HDR behavior, and has undefined interaction with color-calibration software: <https://learn.microsoft.com/en-us/windows/win32/api/wingdi/nf-wingdi-setdevicegammaramp>
- Gamma ramps contain three arrays of 256 16-bit entries and require compatible display hardware and drivers: <https://learn.microsoft.com/en-us/windows/win32/api/wingdi/nf-wingdi-getdevicegammaramp>
- Windows application session volume ranges from 0.0 to 1.0 and cannot provide amplification above unity: <https://learn.microsoft.com/en-us/windows/win32/coreaudio/session-volume-controls>
- Process loopback exists on supported Windows builds but is intentionally not the first-release routing backend: <https://learn.microsoft.com/en-us/windows/win32/api/audioclientactivationparams/ns-audioclientactivationparams-audioclient_process_loopback_params>
