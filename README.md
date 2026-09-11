# EFT Toolkit

A Windows 11 x64 tray utility with two independent halves:

- **Display** — applies F2–F5 gamma-ramp presets to the monitors you select.
- **Audio** (optional) — boosts one routed application's stereo audio through a shared-mode WASAPI
  look-ahead limiter.

The two halves do not depend on each other. Audio being unavailable never removes display
enhancement, and a display failure never stops the audio.

## What this toolkit does not do

- **It never touches Escape from Tarkov's process.** No injection, no hooking, no overlay, no reading
  or writing of another process's memory. The display half writes gamma ramps through the documented
  Windows API, and the audio half works on an audio endpoint — neither one needs to know anything
  about the game beyond its executable name, which is only used to see whether it is running.
- **It does not require administrator rights.** It will never prompt for elevation and has no
  elevation manifest.
- **It does not install, bundle, or update a virtual audio driver.** The audio half needs a
  VB-CABLE-style virtual device that *you* install and configure. This repository ships none, and the
  release artifact contains none.
- **It does not change Windows per-application audio routing for you.** You route the game to the
  virtual playback device in Windows yourself; the panel opens the volume mixer page for you and
  says so.
- **It does not enumerate, open, or hold ASIO interfaces.** Nothing in the audio path touches ASIO.
  An ASIO client running at the same time keeps its device and its settings, and the toolkit will not
  appear in its device list.
- **It never opens an audio endpoint in exclusive mode.** Everything is WASAPI shared mode, so the
  devices it uses stay available to other applications.
- **It captures no audio to disk.** Logs record device identifiers and counters, never samples.

## Requirements

- Windows 11 x64.
- A display whose driver accepts gamma-ramp writes (`SetDeviceGammaRamp`), on SDR or HDR. **HDR is
  supported but experimental**: the ramp is written and the panel marks the monitor
  「HDR（实验性）」, but how a ramp interacts with HDR tone mapping is driver-dependent, and what you
  see is what counts.
- For the audio half: a VB-CABLE-style virtual playback/recording pair, and a physical playback
  device. Both are configured in the panel by identity.

## Using it

1. Start `EftToolkit.App.exe`. The panel opens; the tray icon stays.
2. Tick the monitors you want the display half to drive, then tick 启用显示增强. Nothing visible
   happens until you press a preset key: **F2 is in force and means "the original picture"**.
3. **F2** restores the captured original ramp, **F3** low, **F4** medium, **F5** high. The shortcuts
   are global, so they work with the game in the foreground and the panel hidden.
4. For audio, install a virtual cable yourself, route the game's output to its playback device in
   Windows, then pick the three devices and the executable's file name in the panel and tick
   启用音频增强.
5. Closing the window (✕) **hides it** — the toolkit keeps running in the notification area. Only
  退出 from the tray menu ends it, and that is the only place the toolkit asks a question: if the
   game is playing through the toolkit, exiting stops the forwarding, and you have to route it back
   to a physical device yourself.

### Where things are kept

- Settings and display recovery: `%LOCALAPPDATA%\EftToolkit\`
- Logs: `%LOCALAPPDATA%\EftToolkit\logs\eft-toolkit.log` (also reachable from the panel's footer)

A ramp this toolkit wrote is written back on the next start only if it is still the one it wrote —
after a crash, a ramp that came from somewhere else is left alone.

## Hearing and display safety

Amplifying audio can damage your hearing and your equipment. The default gain is deliberately modest,
and the limiter's ceiling is below full scale, but neither is a guarantee: check the result at a low
volume before you listen at a normal one. The gamma presets brighten the picture; very bright content
on an already bright display can be uncomfortable to look at for long periods, and no preset is worth
eye strain. Nothing here is calibrated for colour-accurate work — do not use it for that.

## Building and testing

```powershell
dotnet restore EftToolkit.sln
dotnet build EftToolkit.sln --configuration Release -warnaserror --no-restore

# Everything except the tests that need real hardware.
dotnet test EftToolkit.sln --configuration Release --no-build --filter 'Category!=Hardware'
```

The tests in `Category=Hardware` write to a real display and open a real audio endpoint. They are
skipped unless explicitly requested, and they are not part of the release gate.

## Publishing

```powershell
pwsh -File scripts/publish.ps1
```

Produces a self-contained `artifacts/publish/win-x64/EftToolkit.App.exe` next to `LICENSE` and this
file. The script refuses to finish if anything that looks like an ASIO component is in the output.

CI runs restore, build, the non-hardware tests, the publish script, and the ASIO rejection checks on
`windows-latest` (`.github/workflows/windows.yml`).

## Before calling a release good

The automated suite cannot establish anything about real gamma ramps, real audio devices, latency, or
ASIO coexistence. `docs/manual-test-checklist.md` is the acceptance process for those, and it is the
only thing that can mark a release as accepted.
