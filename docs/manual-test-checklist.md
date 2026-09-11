# EFT Toolkit — manual acceptance checklist (Windows 11 x64)

What the automated suite cannot establish. Every item here needs a person, a real display, and a real
audio device, so nothing in this file may be reported as passing on the strength of the tests alone.

**Rules for filling this in**

- Record the machine, the Windows build, the GPU driver version, the monitors, and the audio devices
  below before starting. A result without them is not reproducible.
- Fill in **Result** as `PASS`, `FAIL`, or `NOT RUN`, and put the evidence in **Observed** — what was
  seen or heard, not what was expected. A row with `PASS` and no observation is not a result.
- Anything about display ramps is verified by looking at a grey ramp or a familiar dark scene: the
  gamma-ramp API accepts writes that produce no visible change on some drivers, and a readback only
  proves the API stored what it was told.
- Do not run this while EFT is running in a state where a wrong result would cost the player. The
  full-screen test is the only one that needs the game.
- If a row fails, stop and record the failure rather than continuing: several later rows assume the
  earlier ones hold.

## Environment

| Field | Value |
| --- | --- |
| Machine / GPU | |
| Windows build (`winver`) | |
| GPU driver version | |
| Monitors (count, model, connection) | |
| Shared-mode output device | |
| Virtual audio device (name, version) | |
| EFT build | |
| Tester / date | |

## Build and artifact

| # | Check | Result | Observed |
| --- | --- | --- | --- |
| B1 | `dotnet restore EftToolkit.sln` succeeds | | |
| B2 | `dotnet build EftToolkit.sln --configuration Release -warnaserror --no-restore` succeeds with zero warnings | | |
| B3 | `dotnet test EftToolkit.sln --configuration Release --no-build --filter 'Category!=Hardware'` passes, and every skipped test is in `Category=Hardware` | | |
| B4 | `pwsh -File scripts/publish.ps1` produces `artifacts/publish/win-x64/EftToolkit.App.exe` | | |
| B5 | The publish directory contains `LICENSE` and `README.md` | | |
| B6 | No file in the publish directory has `asio` in its name | | |
| B7 | `dotnet list src/EftToolkit.Audio/EftToolkit.Audio.csproj package --include-transitive` shows `NAudio.Wasapi 2.4.0`, and shows neither `NAudio` (meta-package) nor `NAudio.Asio` | | |
| B8 | No virtual audio driver or installer is present in the artifact | | |
| B9 | The application starts from the published artifact without an elevation prompt | | |

## Display: gamma ramps and presets

| # | Check | Result | Observed |
| --- | --- | --- | --- |
| D1 | One display: selecting it and enabling display enhancement writes nothing visible until a preset key is pressed (F2 is in force, showing 「F2 原始 生效中」) | | |
| D2 | F3, F4, and F5 each produce a visibly different, progressively stronger result on that display | | |
| D3 | F2 after each of F3/F4/F5 restores the display to exactly its original appearance | | |
| D4 | Two displays, both selected: F3/F4/F5 change both, and F2 restores both | | |
| D5 | Two displays, one selected: only the selected display changes; the unselected one is untouched | | |
| D6 | A display selected in the panel but physically disconnected shows a row with 未连接 and does not stop the other display from working (per-display failure is visible, not fatal) | | |
| D7 | A display the driver refuses to write shows its own message in its row while the others still switch | | |
| D8 | Hand-edited preset values (伽马 / 阴影提亮 / 输出上限) in an approved range are stored and reapplied when 应用 is pressed | | |
| D9 | A preset value outside the approved range is refused at the field, with a message, and 应用 stays disabled | | |
| D10 | A non-numeric preset value is refused at the field | | |
| D11 | SDR display: the write is visible, and a readback of the ramp matches what was written | | |
| D12 | HDR display: the write is attempted, the 「HDR（实验性）」 badge is shown, and the result is recorded here as observed (this is expected to look wrong on some drivers — record it, do not judge it) | | |

## Display: Windows environment changes

| # | Check | Result | Observed |
| --- | --- | --- | --- |
| E1 | Disconnect and reconnect a selected display: the preset is reapplied without the user touching anything | | |
| E2 | Change the resolution or refresh rate of a selected display: the preset is reapplied | | |
| E3 | Win+L to lock and unlock: the preset is reapplied after unlock | | |
| E4 | Sleep and resume: the preset is reapplied after resume | | |
| E5 | After each of E1–E4, F2 still restores the original ramp of every display | | |
| E6 | Unplug a display while a preset is applied, then plug it back in: no ramp is left applied to it while it is gone | | |

## Display: EFT in the foreground

| # | Check | Result | Observed |
| --- | --- | --- | --- |
| G1 | With EFT running full-screen (borderless and exclusive-fullscreen, if the setup uses both), F3/F4/F5/F2 respond on the first press | | |
| G2 | The same hotkeys work while EFT has focus and the toolkit window is closed/hidden | | |
| G3 | No injection, hooking, overlay, or process modification is observable: EFT's process has no toolkit module loaded (record the check used, e.g. a process-module listing) | | |
| G4 | EFT is not affected by the toolkit starting or exiting while it runs | | |
| G5 | A hotkey that Windows has already claimed by another application is reported as unusable, and the remaining three still work | | |

## Audio: routing and isolation

| # | Check | Result | Observed |
| --- | --- | --- | --- |
| A1 | Audio enhancement refuses to run when the virtual devices are not configured, and the display half keeps working | | |
| A2 | With the virtual devices configured and the game routed to the virtual playback device, enabling audio processes the game's sound and nothing else does | | |
| A3 | Only the executable named in 应用程序 is enhanced; a second application on the same virtual endpoint is not, and its presence is reported as a warning | | |
| A4 | The toolkit does not change Windows per-application routing: 「打开 Windows 音量混合器」 opens the Settings page, and routing is done there by hand | | |
| A5 | Uninstalling or disabling the virtual device while audio is on: the audio half faults, says why, and the display half is unaffected (per the panel, both halves stay independently usable) | | |
| A6 | Disconnecting the physical endpoints while audio is on: the fault is reported, and unplugging/replugging recovers without a restart | | |
| A7 | Changing the Windows default output device while audio is on does not silently reroute or break the toolkit; the state is reported truthfully | | |
| A8 | Bypass (旁路) switches the limiter off without closing the stream, and switching it back on resumes processing without a reconnect | | |
| A9 | After switching audio off and on again, the limiter processes audio unless 旁路 is still ticked | | |
| A10 | No captured audio is ever written to the log: inspect `%LOCALAPPDATA%\EftToolkit\logs` after a session and record what was found | | |

## Audio: ASIO coexistence

| # | Check | Result | Observed |
| --- | --- | --- | --- |
| S1 | With an ASIO client running and holding an interface, the toolkit starts and audio enhancement works | | |
| S2 | Starting the ASIO client after the toolkit, and stopping it, changes nothing about the toolkit's audio | | |
| S3 | The toolkit is never seen in the ASIO client's device list, and no ASIO driver is enumerated, opened, or held by the toolkit (record the check used) | | |
| S4 | The ASIO client keeps its device and its buffer settings throughout, with no dropout or reset attributable to the toolkit | | |

## Audio: stability and latency

| # | Check | Result | Observed |
| --- | --- | --- | --- |
| L1 | 30 minutes of continuous 48 kHz playback through the limiter with the game audio as the source | | |
| L2 | Underrun and overrun counters (欠载 / 溢出) stay at zero, or the values are recorded with the moment they moved | | |
| L3 | Working set and handle count at the start and at the end of the 30 minutes (record both; a steady rise is a leak) | | |
| L4 | The panel's own latency figure (「本工具增加的延迟约」) is at or below 50 ms | | |
| L5 | When the measured additional latency is above 50 ms, a warning is visible in the panel | | |
| L6 | The meters move with the audio and stop at silence rather than holding their last reading | | |
| L7 | The meters do not repaint faster than 10 Hz (record how this was judged) | | |

## Lifecycle

| # | Check | Result | Observed |
| --- | --- | --- | --- |
| W1 | The close button (✕) hides the window instead of exiting: the tray icon remains, and the toolkit keeps working | | |
| W2 | Closing the window while audio is playing does not interrupt the audio | | |
| W3 | Double-clicking the tray icon, and 打开面板 from its menu, bring the window back | | |
| W4 | Launching the application a second time brings the first instance's window up rather than starting a second tray icon | | |
| W5 | 退出 with audio off, or with the target not running, exits without a question | | |
| W6 | 退出 while the game is playing through the toolkit asks whether to exit, in the documented wording | | |
| W7 | Cancelling that question leaves everything running: audio, displays, and the tray icon all unchanged | | |
| W8 | Confirming it exits: the original ramps are restored on every selected display before the process ends | | |
| W9 | After 退出, no gamma ramp written by the toolkit remains applied, and the tray icon is gone | | |
| W10 | Killing the process (Task Manager) and then restarting it: the displays are restored from the recovery file | | |
| W11 | After an ungraceful kill, a ramp the toolkit never wrote is left alone on restart (recovery only touches a fingerprint match) | | |
| W12 | The toolkit runs entirely without administrator rights, and never prompts for elevation | | |
| W13 | Logging off and on again leaves the displays in a sane state (record what happened; the toolkit's teardown on session end is best-effort) | | |

## Sign-off

| Field | Value |
| --- | --- |
| Rows run | |
| Rows failed | |
| Known failures accepted, with reason | |
| Release accepted by / date | |
