# EFT Toolkit Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build a Windows 11 x64 tray utility that safely applies F2-F5 gamma-ramp presets to selected displays and optionally boosts one virtually routed application's stereo audio through a shared-mode WASAPI limiter.

**Architecture:** A .NET 8 WPF shell coordinates independent Display and Audio modules through Core contracts. Display uses narrow Win32 adapters and persisted recovery snapshots; Audio uses only the `NAudio.Wasapi` package, a custom float DSP pipeline, and a separately installed virtual audio endpoint. Platform.Windows owns global hotkeys, Windows event notifications, and single-instance activation.

**Tech Stack:** C# 12, .NET 8, WPF, Win32 P/Invoke, NAudio.Wasapi 2.4.0, xUnit v3 4.0.0, Microsoft.NET.Test.Sdk 17.14.1, GitHub Actions on `windows-latest`.

**Spec:** `docs/superpowers/specs/2026-09-11-eft-toolkit-design.md`

## Global Constraints

- Target Windows 11 x64 only with TFM `net8.0-windows10.0.22000.0` and `PlatformTarget=x64`.
- Run without administrator privileges and do not add elevation manifests.
- Never inject into, hook, overlay, read memory from, or write memory to EFT.
- Display changes are global only to the monitors selected by the user.
- F2 restores the captured original ramp; F3, F4, and F5 apply low, medium, and high presets.
- HDR is allowed but visibly marked experimental; do not block gamma-ramp writes in HDR.
- Audio is optional, checks dependencies only when enabled, and faults independently of Display.
- Reference `NAudio.Wasapi` 2.4.0 directly. Do not reference the `NAudio` meta-package or `NAudio.Asio`.
- Open capture and render endpoints only in WASAPI shared mode. Never fall back to exclusive mode.
- Do not enumerate, initialize, configure, or hold ASIO interfaces.
- Do not install, bundle, update, or license a virtual audio driver.
- Do not automatically change Windows per-application audio routing.
- Do not add Windows startup registration or an auto-start setting in the first release.
- Closing the window hides it; only the tray Exit command performs full shutdown.
- Use test-driven development for every behavior and commit after every task.
- Execute build, automated tests, publish, and hardware acceptance on Windows 11 because the current planning host has no .NET SDK and is not Windows.

## File and Dependency Map

The implementation creates these top-level assets:

- `EftToolkit.sln`: solution containing five production projects and one test project.
- `Directory.Build.props`: common target framework, x64, nullable, analyzer, and deterministic-build settings.
- `Directory.Packages.props`: centralized package versions.
- `src/EftToolkit.Core`: configuration, module state, lifecycle contracts, persistence, and logging.
- `src/EftToolkit.Display`: pure ramp math, display orchestration, snapshots, and the GDI/DisplayConfig gateway.
- `src/EftToolkit.Audio`: pure DSP, audio profiles, device/process discovery, module orchestration, and NAudio shared streams.
- `src/EftToolkit.Platform.Windows`: message sink, hotkeys, display/power/session events, and single-instance activation.
- `src/EftToolkit.App`: WPF composition root, panel, view models, tray behavior, and shutdown prompts.
- `tests/EftToolkit.Tests`: unit and component tests; hardware APIs are always behind fakes in automated tests.
- `scripts/publish.ps1`: repeatable self-contained x64 ZIP-ready publish.
- `.github/workflows/windows.yml`: restore, build, test, and publish-artifact validation.
- `docs/manual-test-checklist.md`: Windows display, audio, ASIO coexistence, latency, and shutdown acceptance procedure.

Project references are directional:

```text
EftToolkit.App -> Core, Display, Audio, Platform.Windows
EftToolkit.Display -> Core
EftToolkit.Audio -> Core, NAudio.Wasapi
EftToolkit.Platform.Windows -> Core
EftToolkit.Tests -> Core, Display, Audio, Platform.Windows, App
```

`Core` references no UI, Win32, or audio package. `Display` and `Audio` do not reference each other.

---

### Task 1: Solution skeleton and versioned domain configuration

**Files:**
- Create: `EftToolkit.sln`
- Create: `Directory.Build.props`
- Create: `Directory.Packages.props`
- Create: `src/EftToolkit.Core/EftToolkit.Core.csproj`
- Create: `src/EftToolkit.Display/EftToolkit.Display.csproj`
- Create: `src/EftToolkit.Audio/EftToolkit.Audio.csproj`
- Create: `src/EftToolkit.Platform.Windows/EftToolkit.Platform.Windows.csproj`
- Create: `src/EftToolkit.App/EftToolkit.App.csproj`
- Create: `tests/EftToolkit.Tests/EftToolkit.Tests.csproj`
- Create: `src/EftToolkit.Core/Modules/ModuleState.cs`
- Create: `src/EftToolkit.Core/Modules/ModuleStatus.cs`
- Create: `src/EftToolkit.Core/Modules/IToolkitModule.cs`
- Create: `src/EftToolkit.Core/Modules/IDisplayController.cs`
- Create: `src/EftToolkit.Core/Modules/IAudioController.cs`
- Create: `src/EftToolkit.Core/Display/DisplayPresetKind.cs`
- Create: `src/EftToolkit.Core/Configuration/ToolkitOptions.cs`
- Create: `src/EftToolkit.Core/Configuration/OptionsValidator.cs`
- Test: `tests/EftToolkit.Tests/Core/OptionsValidatorTests.cs`

**Interfaces:**
- Produces: `ModuleState`, `ModuleStatus`, `IToolkitModule`, `IDisplayController`, `IAudioController`, `DisplayPresetKind`, `ToolkitOptions`, `DisplayPresetOptions`, `AudioProfileOptions`, `AudioLimiterOptions`, and `OptionsValidator.Validate(ToolkitOptions)`.
- Consumes: no application code; this task establishes all project boundaries.

- [ ] **Step 1: Create the solution and project files, then write the failing defaults test**

Use central package management and reference only the WASAPI subset:

```xml
<!-- Directory.Packages.props -->
<Project>
  <PropertyGroup>
    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
  </PropertyGroup>
  <ItemGroup>
    <PackageVersion Include="NAudio.Wasapi" Version="2.4.0" />
    <PackageVersion Include="Microsoft.NET.Test.Sdk" Version="17.14.1" />
    <PackageVersion Include="xunit.v3" Version="4.0.0" />
  </ItemGroup>
</Project>
```

Use this common build configuration:

```xml
<!-- Directory.Build.props -->
<Project>
  <PropertyGroup>
    <TargetFramework>net8.0-windows10.0.22000.0</TargetFramework>
    <PlatformTarget>x64</PlatformTarget>
    <LangVersion>12.0</LangVersion>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <EnableWindowsTargeting>true</EnableWindowsTargeting>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <Deterministic>true</Deterministic>
  </PropertyGroup>
</Project>
```

Create the solution and projects with these commands, remove all generated `Class1.cs` and `UnitTest1.cs` files, then add project references according to the dependency map:

```powershell
dotnet new sln -n EftToolkit
dotnet new classlib -n EftToolkit.Core -o src/EftToolkit.Core
dotnet new classlib -n EftToolkit.Display -o src/EftToolkit.Display
dotnet new classlib -n EftToolkit.Audio -o src/EftToolkit.Audio
dotnet new classlib -n EftToolkit.Platform.Windows -o src/EftToolkit.Platform.Windows
dotnet new wpf -n EftToolkit.App -o src/EftToolkit.App
dotnet new classlib -n EftToolkit.Tests -o tests/EftToolkit.Tests
dotnet sln EftToolkit.sln add src/EftToolkit.Core/EftToolkit.Core.csproj src/EftToolkit.Display/EftToolkit.Display.csproj src/EftToolkit.Audio/EftToolkit.Audio.csproj src/EftToolkit.Platform.Windows/EftToolkit.Platform.Windows.csproj src/EftToolkit.App/EftToolkit.App.csproj tests/EftToolkit.Tests/EftToolkit.Tests.csproj
dotnet add src/EftToolkit.Display/EftToolkit.Display.csproj reference src/EftToolkit.Core/EftToolkit.Core.csproj
dotnet add src/EftToolkit.Audio/EftToolkit.Audio.csproj reference src/EftToolkit.Core/EftToolkit.Core.csproj
dotnet add src/EftToolkit.Platform.Windows/EftToolkit.Platform.Windows.csproj reference src/EftToolkit.Core/EftToolkit.Core.csproj
dotnet add src/EftToolkit.App/EftToolkit.App.csproj reference src/EftToolkit.Core/EftToolkit.Core.csproj src/EftToolkit.Display/EftToolkit.Display.csproj src/EftToolkit.Audio/EftToolkit.Audio.csproj src/EftToolkit.Platform.Windows/EftToolkit.Platform.Windows.csproj
dotnet add tests/EftToolkit.Tests/EftToolkit.Tests.csproj reference src/EftToolkit.Core/EftToolkit.Core.csproj src/EftToolkit.Display/EftToolkit.Display.csproj src/EftToolkit.Audio/EftToolkit.Audio.csproj src/EftToolkit.Platform.Windows/EftToolkit.Platform.Windows.csproj src/EftToolkit.App/EftToolkit.App.csproj
```

Set `<OutputType>WinExe</OutputType>`, `<UseWPF>true</UseWPF>`, and `<UseWindowsForms>true</UseWindowsForms>` only in App. Add `NAudio.Wasapi` only to Audio. Add `Microsoft.NET.Test.Sdk` and `xunit.v3` only to Tests, with `<IsTestProject>true</IsTestProject>` and `<IsPackable>false</IsPackable>`. Every `PackageReference` omits a version because `Directory.Packages.props` owns it.

Write this first test:

```csharp
[Fact]
public void CreateDefault_uses_approved_display_and_audio_values()
{
    ToolkitOptions options = ToolkitOptions.CreateDefault();

    Assert.Equal(new DisplayPresetOptions(1.15, 0.00, 1.00), options.Display.Low);
    Assert.Equal(new DisplayPresetOptions(1.35, 0.01, 1.00), options.Display.Medium);
    Assert.Equal(new DisplayPresetOptions(1.55, 0.02, 1.00), options.Display.High);
    Assert.Equal(12.0, options.Audio.Limiter.InputGainDb);
    Assert.Equal(-12.0, options.Audio.Limiter.ThresholdDbFs);
    Assert.Equal(-1.0, options.Audio.Limiter.CeilingDbFs);
    Assert.Equal("EscapeFromTarkov", options.Audio.Profiles.Single().ExecutableName);
}
```

- [ ] **Step 2: Run the focused test and verify the red state**

Run: `dotnet test tests/EftToolkit.Tests/EftToolkit.Tests.csproj --filter FullyQualifiedName~OptionsValidatorTests`

Expected: FAIL because `ToolkitOptions` and its defaults do not exist.

- [ ] **Step 3: Implement the configuration records and validation contract**

Use these exact public shapes:

```csharp
public enum ModuleState { Disabled, Starting, Active, Bypass, Degraded, Faulted, Stopping }

public sealed record ModuleStatus(ModuleState State, string? Message = null, string? ErrorCode = null);

public interface IToolkitModule : IAsyncDisposable
{
    ModuleStatus Status { get; }
    event EventHandler<ModuleStatus>? StatusChanged;
    Task EnableAsync(CancellationToken cancellationToken);
    Task DisableAsync(CancellationToken cancellationToken);
}

public enum DisplayPresetKind { Original, Low, Medium, High }

public interface IDisplayController : IToolkitModule
{
    Task ApplyPresetAsync(DisplayPresetKind preset, CancellationToken cancellationToken);
    Task RefreshAndReapplyAsync(CancellationToken cancellationToken);
    Task RecoverAsync(CancellationToken cancellationToken);
}

public interface IAudioController : IToolkitModule
{
    bool IsTargetRunning { get; }
    bool IsRouteOpen { get; }
    Task SetBypassAsync(bool bypass, CancellationToken cancellationToken);
}

public sealed record DisplayPresetOptions(double Gamma, double ShadowLift, double OutputCeiling);

public sealed record DisplayOptions(
    bool Enabled,
    IReadOnlyList<string> SelectedDisplayIds,
    DisplayPresetOptions Low,
    DisplayPresetOptions Medium,
    DisplayPresetOptions High);

public sealed record AudioLimiterOptions(
    double InputGainDb,
    double ThresholdDbFs,
    double Ratio,
    double KneeDb,
    double LookAheadMs,
    double AttackMs,
    double ReleaseMs,
    double CeilingDbFs);

public sealed record AudioProfileOptions(
    string Id,
    string DisplayName,
    string ExecutableName,
    string? VirtualRenderEndpointId,
    string? VirtualCaptureEndpointId,
    string? PhysicalRenderEndpointId);

public sealed record AudioOptions(
    bool Enabled,
    string ActiveProfileId,
    AudioLimiterOptions Limiter,
    IReadOnlyList<AudioProfileOptions> Profiles);

public sealed record ToolkitOptions(int SchemaVersion, DisplayOptions Display, AudioOptions Audio)
{
    public const int CurrentSchemaVersion = 1;
    public static ToolkitOptions CreateDefault();
}
```

`OptionsValidator.Validate` returns a new sanitized object. Enforce gamma `0.50..3.00`, shadow lift `0.00..0.20`, output ceiling `0.50..1.00`, gain `0..24 dB`, threshold `-24..-1 dBFS`, ratio `1..50`, knee `0..12 dB`, look-ahead `0..20 ms`, attack `0.1..50 ms`, release `10..1000 ms`, and ceiling `-12..-0.1 dBFS`. Reject duplicate or blank profile IDs and ensure `ActiveProfileId` exists. Replace each invalid value with its approved default; do not partially clamp malformed persisted values.

- [ ] **Step 4: Add boundary theories and run the complete test project**

Add `[Theory]` cases for every lower/upper bound and for invalid profile IDs. Run: `dotnet test tests/EftToolkit.Tests/EftToolkit.Tests.csproj`

Expected: PASS, with tests covering all default values and validation bounds.

- [ ] **Step 5: Verify package isolation and commit**

Run: `dotnet list src/EftToolkit.Audio/EftToolkit.Audio.csproj package --include-transitive | findstr /I "NAudio.Asio"`

Expected: no output and exit code 1 from `findstr`. Then run `dotnet build EftToolkit.sln -warnaserror` and commit:

```bash
git add EftToolkit.sln Directory.Build.props Directory.Packages.props src tests
git commit -m "build: scaffold EFT Toolkit solution"
```

### Task 2: Atomic settings and structured rolling logs

**Files:**
- Create: `src/EftToolkit.Core/Configuration/IOptionsStore.cs`
- Create: `src/EftToolkit.Core/Configuration/JsonOptionsStore.cs`
- Create: `src/EftToolkit.Core/Diagnostics/IAppLogger.cs`
- Create: `src/EftToolkit.Core/Diagnostics/JsonLineLogger.cs`
- Test: `tests/EftToolkit.Tests/Core/JsonOptionsStoreTests.cs`
- Test: `tests/EftToolkit.Tests/Core/JsonLineLoggerTests.cs`

**Interfaces:**
- Consumes: `ToolkitOptions`, `OptionsValidator.Validate` from Task 1.
- Produces: `IOptionsStore.LoadAsync`, `IOptionsStore.SaveAsync`, `IAppLogger.Write`, and deterministic AppData file locations.

- [ ] **Step 1: Write failing persistence tests**

```csharp
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
public async Task SaveAsync_replaces_settings_atomically_and_leaves_no_temp_file()
{
    var store = new JsonOptionsStore(_directory, TimeProvider.System);
    await store.SaveAsync(ToolkitOptions.CreateDefault(), CancellationToken.None);

    Assert.True(File.Exists(_settingsPath));
    Assert.False(File.Exists(_settingsPath + ".tmp"));
}
```

- [ ] **Step 2: Run the persistence tests and verify they fail**

Run: `dotnet test tests/EftToolkit.Tests/EftToolkit.Tests.csproj --filter FullyQualifiedName~JsonOptionsStoreTests`

Expected: FAIL because the store is undefined.

- [ ] **Step 3: Implement the options store**

Use this contract:

```csharp
public interface IOptionsStore
{
    Task<ToolkitOptions> LoadAsync(CancellationToken cancellationToken);
    Task SaveAsync(ToolkitOptions options, CancellationToken cancellationToken);
}
```

Default production root is `Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EftToolkit")`. Serialize camelCase JSON to `settings.json`. Save to `settings.json.tmp`, call `FlushAsync`, close the stream, then `File.Move(temp, target, true)`. On `JsonException` or an unsupported schema version, move the original to `settings.corrupt.{yyyyMMdd-HHmmssfff}.json`, return validated defaults, and log the preserved path. Never delete a corrupt file.

- [ ] **Step 4: Write failing logger rotation and redaction tests**

Test that a logger writes one JSON object per line with UTC timestamp, level, event name, and properties; rolls `eft-toolkit.log` at 5 MiB; keeps five archives; and rejects property keys named `audioSamples`, `pcm`, or `bufferBytes` using ordinal-ignore-case matching.

Run: `dotnet test tests/EftToolkit.Tests/EftToolkit.Tests.csproj --filter FullyQualifiedName~JsonLineLoggerTests`

Expected: FAIL because the logger is undefined.

- [ ] **Step 5: Implement logging, run tests, and commit**

```csharp
public interface IAppLogger : IAsyncDisposable
{
    void Write(LogLevel level, string eventName, IReadOnlyDictionary<string, object?>? properties = null, Exception? exception = null);
}

public enum LogLevel { Debug, Information, Warning, Error, Critical }
```

Use a `SemaphoreSlim` around UTF-8 append and rotation. Serialize exceptions as type, message, HResult, and stack trace. Do not serialize captured audio or byte buffers. Run `dotnet test tests/EftToolkit.Tests/EftToolkit.Tests.csproj`, then commit:

```bash
git add src/EftToolkit.Core tests/EftToolkit.Tests/Core
git commit -m "feat: add atomic settings and structured logs"
```

### Task 3: Gamma-ramp model and preset composition

**Files:**
- Create: `src/EftToolkit.Display/Gamma/GammaRamp.cs`
- Create: `src/EftToolkit.Display/Gamma/GammaRampComposer.cs`
- Create: `src/EftToolkit.Display/Gamma/GammaRampFingerprint.cs`
- Test: `tests/EftToolkit.Tests/Display/GammaRampComposerTests.cs`
- Test: `tests/EftToolkit.Tests/Display/GammaRampFingerprintTests.cs`

**Interfaces:**
- Consumes: `DisplayPresetOptions` and `DisplayPresetKind` from Task 1.
- Produces: immutable `GammaRamp`, `GammaRampComposer.Compose`, and `GammaRampFingerprint.Compute`.

- [ ] **Step 1: Write failing identity, defaults, monotonicity, and immutability tests**

```csharp
[Fact]
public void Compose_uses_transformed_index_against_original_ramp()
{
    GammaRamp original = GammaRamp.CreateIdentity();
    var preset = new DisplayPresetOptions(1.35, 0.01, 1.00);

    GammaRamp result = GammaRampComposer.Compose(original, preset);

    Assert.True(result.Red[32] > original.Red[32]);
    Assert.True(result.Red.Zip(result.Red.Skip(1)).All(pair => pair.First <= pair.Second));
    Assert.Equal(result.Red, result.Green);
    Assert.Equal(result.Green, result.Blue);
}

[Fact]
public void Constructor_copies_input_channels()
{
    ushort[] channel = Enumerable.Range(0, 256).Select(i => (ushort)(i * 257)).ToArray();
    GammaRamp ramp = new(channel, channel, channel);
    channel[100] = 0;
    Assert.NotEqual((ushort)0, ramp.Red[100]);
}
```

- [ ] **Step 2: Run and verify the red state**

Run: `dotnet test tests/EftToolkit.Tests/EftToolkit.Tests.csproj --filter FullyQualifiedName~GammaRamp`

Expected: FAIL because the gamma types do not exist.

- [ ] **Step 3: Implement the exact composition algorithm**

`GammaRamp` owns three copied arrays of exactly 256 entries and returns read-only views. `CreateIdentity` maps index `i` to `i * 257`.

For each normalized index `x = i / 255.0`, compute:

```csharp
double transformed = options.ShadowLift
    + (1.0 - options.ShadowLift) * Math.Pow(x, 1.0 / options.Gamma);
double capped = Math.Clamp(transformed, 0.0, options.OutputCeiling);
double sourcePosition = capped * 255.0;
int lower = (int)Math.Floor(sourcePosition);
int upper = Math.Min(255, lower + 1);
double fraction = sourcePosition - lower;
ushort output = (ushort)Math.Clamp(
    Math.Round(channel[lower] + (channel[upper] - channel[lower]) * fraction),
    ushort.MinValue,
    ushort.MaxValue);
```

After interpolation, perform a forward pass setting `result[i] = Max(result[i], result[i - 1])` independently for R, G, and B. Fingerprints are uppercase SHA-256 hex over the 1536 little-endian channel bytes in R-G-B order.

- [ ] **Step 4: Add edge-case theories and run tests**

Cover non-linear original ramps, output ceilings, maximum allowed lift/gamma, exact identity composition at gamma 1/lift 0/ceiling 1, and constructor rejection of arrays not exactly 256 entries.

Run: `dotnet test tests/EftToolkit.Tests/EftToolkit.Tests.csproj --filter FullyQualifiedName~GammaRamp`

Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/EftToolkit.Display/Gamma tests/EftToolkit.Tests/Display
git commit -m "feat: compose safe display gamma ramps"
```

### Task 4: Windows display discovery and gamma gateway

**Files:**
- Create: `src/EftToolkit.Display/Devices/DisplayDescriptor.cs`
- Create: `src/EftToolkit.Display/Devices/IDisplayGammaGateway.cs`
- Create: `src/EftToolkit.Display/Devices/Win32DisplayGammaGateway.cs`
- Create: `src/EftToolkit.Display/Interop/Gdi32.cs`
- Create: `src/EftToolkit.Display/Interop/User32.cs`
- Create: `src/EftToolkit.Display/Interop/DisplayConfig.cs`
- Test: `tests/EftToolkit.Tests/Display/Win32StructureLayoutTests.cs`
- Test: `tests/EftToolkit.Tests/Display/DisplayDescriptorTests.cs`

**Interfaces:**
- Consumes: `GammaRamp` and `GammaRampFingerprint` from Task 3.
- Produces: `DisplayDescriptor` and `IDisplayGammaGateway` for DisplayModule.

- [ ] **Step 1: Define the gateway contract and write failing boundary tests**

```csharp
public sealed record DisplayDescriptor(
    string StableId,
    string GdiDeviceName,
    string FriendlyName,
    bool IsConnected,
    bool IsHdr,
    bool SupportsGammaRamp);

public sealed record GammaWriteResult(bool ApiAccepted, bool ReadbackMatched, int? Win32Error, string? Message);

public interface IDisplayGammaGateway
{
    Task<IReadOnlyList<DisplayDescriptor>> EnumerateAsync(CancellationToken cancellationToken);
    Task<GammaRamp> ReadAsync(DisplayDescriptor display, CancellationToken cancellationToken);
    Task<GammaWriteResult> WriteAsync(DisplayDescriptor display, GammaRamp ramp, CancellationToken cancellationToken);
}
```

Test that empty target paths fall back to `adapterLuid:targetId`, friendly names fall back to GDI names, and all native structs have the sizes asserted from the Windows SDK headers used by the implementation build.

- [ ] **Step 2: Run tests and verify the red state**

Run: `dotnet test tests/EftToolkit.Tests/EftToolkit.Tests.csproj --filter "FullyQualifiedName~DisplayDescriptorTests|FullyQualifiedName~Win32StructureLayoutTests"`

Expected: FAIL because descriptors and interop structures are undefined.

- [ ] **Step 3: Implement display enumeration and stable identity mapping**

Use `GetDisplayConfigBufferSizes`, `QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS)`, and `DisplayConfigGetDeviceInfo` with `DISPLAYCONFIG_TARGET_DEVICE_NAME` to obtain monitor device paths and friendly names. Map source GDI names using `DISPLAYCONFIG_SOURCE_DEVICE_NAME`. Query `DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO` for HDR state; if unavailable, set `IsHdr=false` and log the error without excluding the monitor.

Declare source-generated interop where supported:

```csharp
[LibraryImport("gdi32.dll", EntryPoint = "CreateDCW", StringMarshalling = StringMarshalling.Utf16)]
internal static partial nint CreateDC(string? driver, string device, string? output, nint initData);

[LibraryImport("gdi32.dll", SetLastError = true)]
[return: MarshalAs(UnmanagedType.Bool)]
internal static partial bool GetDeviceGammaRamp(nint hdc, nint ramp);

[LibraryImport("gdi32.dll", SetLastError = true)]
[return: MarshalAs(UnmanagedType.Bool)]
internal static partial bool SetDeviceGammaRamp(nint hdc, nint ramp);

[LibraryImport("gdi32.dll")]
[return: MarshalAs(UnmanagedType.Bool)]
internal static partial bool DeleteDC(nint hdc);
```

Allocate exactly `3 * 256 * sizeof(ushort)` unmanaged bytes, copy R-G-B in sequence, and always release both allocation and HDC in `finally` blocks. Never share an HDC across calls or threads.

- [ ] **Step 4: Implement write/readback behavior and Windows-only smoke test**

`WriteAsync` calls `SetDeviceGammaRamp`, then immediately calls `GetDeviceGammaRamp` on a fresh HDC and compares fingerprints. Preserve three outcomes: API rejected, API accepted but readback differed, and verified. Do not infer final visible HDR output from readback.

Add a skipped-by-default test with trait `Category=Hardware` that enumerates and reads each ramp without writing it. Run on a Windows 11 development machine:

`dotnet test tests/EftToolkit.Tests/EftToolkit.Tests.csproj --filter Category=Hardware`

Expected: PASS after the developer explicitly enables the hardware test environment variable `EFT_TOOLKIT_HARDWARE_TESTS=1`; otherwise the test returns without touching hardware.

- [ ] **Step 5: Run all non-hardware tests and commit**

Run: `dotnet test tests/EftToolkit.Tests/EftToolkit.Tests.csproj --filter "Category!=Hardware"`

Expected: PASS. Commit:

```bash
git add src/EftToolkit.Display tests/EftToolkit.Tests/Display
git commit -m "feat: add Windows display gamma gateway"
```

### Task 5: Display recovery snapshots and module orchestration

**Files:**
- Create: `src/EftToolkit.Display/Recovery/DisplayRecoverySnapshot.cs`
- Create: `src/EftToolkit.Display/Recovery/IDisplayRecoveryStore.cs`
- Create: `src/EftToolkit.Display/Recovery/JsonDisplayRecoveryStore.cs`
- Create: `src/EftToolkit.Display/DisplayStatus.cs`
- Create: `src/EftToolkit.Display/DisplayModule.cs`
- Test: `tests/EftToolkit.Tests/Display/JsonDisplayRecoveryStoreTests.cs`
- Test: `tests/EftToolkit.Tests/Display/DisplayModuleTests.cs`

**Interfaces:**
- Consumes: `IToolkitModule`, `DisplayOptions`, `IAppLogger`, `IDisplayGammaGateway`, gamma composition and fingerprints.
- Produces: `DisplayModule.ApplyPresetAsync`, `DisplayModule.RefreshAndReapplyAsync`, per-monitor `DisplayStatus`, and crash recovery.

- [ ] **Step 1: Write failing recovery safety tests**

```csharp
[Fact]
public async Task RecoverAsync_restores_only_when_current_matches_last_toolkit_write()
{
    FakeDisplayGammaGateway gateway = FakeDisplayGammaGateway.OneDisplay(
        current: _toolkitRamp,
        original: _originalRamp);
    await _recoveryStore.SaveAsync(Snapshot(_originalRamp, _toolkitRamp), CancellationToken.None);
    var module = CreateModule(gateway);

    await module.RecoverAsync(CancellationToken.None);

    Assert.Equal(_originalRamp, gateway.LastWrittenRamp);
}

[Fact]
public async Task RecoverAsync_preserves_external_change()
{
    FakeDisplayGammaGateway gateway = FakeDisplayGammaGateway.OneDisplay(
        current: GammaRamp.CreateIdentity(),
        original: _originalRamp);
    await _recoveryStore.SaveAsync(Snapshot(_originalRamp, _toolkitRamp), CancellationToken.None);

    await CreateModule(gateway).RecoverAsync(CancellationToken.None);

    Assert.Null(gateway.LastWrittenRamp);
}
```

Also test checksum rejection and stable-ID mismatch.

- [ ] **Step 2: Run recovery tests and verify failure**

Run: `dotnet test tests/EftToolkit.Tests/EftToolkit.Tests.csproj --filter FullyQualifiedName~DisplayRecovery`

Expected: FAIL because the recovery store and module do not exist.

- [ ] **Step 3: Implement checksummed recovery persistence**

Use one `display-recovery.json` file under the Core AppData directory. Each entry contains stable ID, original ramp as Base64 little-endian bytes, last-written fingerprint, captured UTC time, and an entry checksum over stable ID plus ramp bytes plus fingerprint plus timestamp. Save atomically using the same temp-and-replace sequence as settings. A corrupt recovery file is renamed with `.corrupt.{timestamp}.json` and never applied.

Use these persisted shapes:

```csharp
public sealed record DisplayRecoveryEntry(
    string StableId,
    string OriginalRampBase64,
    string LastWrittenFingerprint,
    DateTimeOffset CapturedAtUtc,
    string Checksum);

public sealed record DisplayRecoverySnapshot(int SchemaVersion, IReadOnlyList<DisplayRecoveryEntry> Displays);

public interface IDisplayRecoveryStore
{
    Task<DisplayRecoverySnapshot?> LoadAsync(CancellationToken cancellationToken);
    Task SaveAsync(DisplayRecoverySnapshot snapshot, CancellationToken cancellationToken);
    Task RemoveAsync(IReadOnlySet<string> restoredStableIds, CancellationToken cancellationToken);
}
```

- [ ] **Step 4: Write failing module tests for partial failure and coalescing**

Test these exact behaviors:

- Enable captures only selected connected displays and writes recovery before applying a preset.
- F2 writes the exact original ramp while leaving the module enabled.
- One failed display does not block another display.
- Three queued preset requests ending in High produce High as final logical state and do not require all intermediate writes.
- Disable and Dispose are idempotent and restore originals once.
- `RefreshAndReapplyAsync` re-enumerates and writes the current logical preset to matching selected IDs.

- [ ] **Step 5: Implement DisplayModule with a latest-value worker**

Expose:

```csharp
public sealed class DisplayModule : IDisplayController
{
    public DisplayPresetKind CurrentPreset { get; }
    public IReadOnlyList<DisplayStatus> Displays { get; }
    public event EventHandler? DisplaysChanged;
    public Task ApplyPresetAsync(DisplayPresetKind preset, CancellationToken cancellationToken);
    public Task RefreshAndReapplyAsync(CancellationToken cancellationToken);
    public Task RecoverAsync(CancellationToken cancellationToken);
}
```

Use `record DisplayStatus(DisplayDescriptor Display, bool Selected, DisplayPresetKind Preset, GammaWriteResult? LastResult, string? Message)` for each Panel row.

Use a bounded `Channel<DisplayPresetKind>` of capacity one with `FullMode=DropOldest`. One background reader serializes writes. Store the logical preset before queuing. During disable, complete the channel, wait for the reader, and restore each captured original once. Remove a display's recovery entry only after restoration is accepted and read back successfully; preserve failed entries for next-launch recovery.

- [ ] **Step 6: Run tests and commit**

Run: `dotnet test tests/EftToolkit.Tests/EftToolkit.Tests.csproj --filter FullyQualifiedName~Display`

Expected: PASS. Commit:

```bash
git add src/EftToolkit.Display tests/EftToolkit.Tests/Display
git commit -m "feat: orchestrate multi-display presets and recovery"
```

### Task 6: Global hotkeys and Windows environment events

**Files:**
- Create: `src/EftToolkit.Platform.Windows/Messaging/WindowsMessage.cs`
- Create: `src/EftToolkit.Platform.Windows/Messaging/WindowsMessageDecoder.cs`
- Create: `src/EftToolkit.Platform.Windows/Messaging/WindowsMessageSink.cs`
- Create: `src/EftToolkit.Core/Platform/IHotkeyService.cs`
- Create: `src/EftToolkit.Platform.Windows/Hotkeys/GlobalHotkeyService.cs`
- Create: `src/EftToolkit.Core/Platform/IPlatformEventSource.cs`
- Create: `src/EftToolkit.Platform.Windows/Events/PlatformEventSource.cs`
- Create: `src/EftToolkit.Platform.Windows/Interop/NativeMethods.cs`
- Test: `tests/EftToolkit.Tests/Platform/WindowsMessageDecoderTests.cs`
- Test: `tests/EftToolkit.Tests/Platform/GlobalHotkeyServiceTests.cs`

**Interfaces:**
- Consumes: `DisplayPresetKind` from Task 1 and `IAppLogger` from Task 2.
- Produces: F2-F5 preset events and one debounced display-environment-changed event.

- [ ] **Step 1: Write failing pure message-decoder tests**

Map these constants exactly: `WM_HOTKEY=0x0312`, `WM_DISPLAYCHANGE=0x007E`, `WM_POWERBROADCAST=0x0218`, `PBT_APMRESUMEAUTOMATIC=0x0012`, `WM_WTSSESSION_CHANGE=0x02B1`, and `WTS_SESSION_UNLOCK=0x0008`. Hotkey IDs `0xEF20..0xEF23` map to Original, Low, Medium, High.

```csharp
[Theory]
[InlineData(0xEF20, DisplayPresetKind.Original)]
[InlineData(0xEF21, DisplayPresetKind.Low)]
[InlineData(0xEF22, DisplayPresetKind.Medium)]
[InlineData(0xEF23, DisplayPresetKind.High)]
public void DecodeHotkey_maps_registered_ids(int id, DisplayPresetKind expected)
{
    Assert.Equal(expected, WindowsMessageDecoder.DecodeHotkey(id));
}
```

- [ ] **Step 2: Run focused tests and verify failure**

Run: `dotnet test tests/EftToolkit.Tests/EftToolkit.Tests.csproj --filter "FullyQualifiedName~WindowsMessageDecoder|FullyQualifiedName~GlobalHotkey"`

Expected: FAIL because the platform services do not exist.

- [ ] **Step 3: Implement a dedicated hidden message sink and hotkeys**

Create a Win32 message-only window on an STA thread; do not bind hotkeys to MainWindow so hiding or recreating UI cannot stop them. Register virtual keys F2 `0x71`, F3 `0x72`, F4 `0x73`, F5 `0x74` with `MOD_NOREPEAT=0x4000` and no other modifier. Record success per key and keep successful registrations when another key collides.

```csharp
public interface IHotkeyService : IAsyncDisposable
{
    IReadOnlyDictionary<DisplayPresetKind, bool> Registrations { get; }
    event EventHandler<DisplayPresetKind>? PresetRequested;
    Task RegisterAsync(CancellationToken cancellationToken);
    Task UnregisterAsync(CancellationToken cancellationToken);
}
```

Register the message window with `WTSRegisterSessionNotification(messageWindowHandle, NOTIFY_FOR_THIS_SESSION)`, where `NOTIFY_FOR_THIS_SESSION=0`, and always unregister it before destruction.

- [ ] **Step 4: Implement environment events and debounce**

```csharp
public interface IPlatformEventSource : IAsyncDisposable
{
    event EventHandler? DisplayEnvironmentChanged;
    Task StartAsync(CancellationToken cancellationToken);
    Task StopAsync(CancellationToken cancellationToken);
}
```

Emit for display changes, automatic resume, and session unlock. Collapse events arriving within 500 ms into one notification. Never perform gamma I/O on the message-window thread.

- [ ] **Step 5: Run tests and commit**

Use an injected native registration adapter in tests to simulate one failed key and verify the others remain registered. Run `dotnet test tests/EftToolkit.Tests/EftToolkit.Tests.csproj --filter "FullyQualifiedName~Platform|FullyQualifiedName~Hotkey"`, then commit:

```bash
git add src/EftToolkit.Core/Platform src/EftToolkit.Platform.Windows tests/EftToolkit.Tests/Platform
git commit -m "feat: add global display hotkeys and platform events"
```

### Task 7: Stereo gain and linked look-ahead limiter

**Files:**
- Create: `src/EftToolkit.Audio/Dsp/AudioMath.cs`
- Create: `src/EftToolkit.Audio/Dsp/AudioProcessorMetrics.cs`
- Create: `src/EftToolkit.Audio/Dsp/StereoLinkedLimiter.cs`
- Test: `tests/EftToolkit.Tests/Audio/StereoLinkedLimiterTests.cs`

**Interfaces:**
- Consumes: `AudioLimiterOptions` from Task 1.
- Produces: `StereoLinkedLimiter.Process(Span<float>)`, reset/reconfigure methods, and immutable metering snapshots.

- [ ] **Step 1: Write failing gain, linkage, ceiling, finite-value, and reset tests**

```csharp
[Fact]
public void Process_applies_identical_gain_reduction_to_both_channels()
{
    var limiter = new StereoLinkedLimiter(48_000, TestSettings(inputGainDb: 12, lookAheadMs: 0));
    float[] interleaved = [0.90f, 0.09f, 0.90f, 0.09f];

    limiter.Process(interleaved);

    Assert.Equal(interleaved[0] / interleaved[1], 10.0f, 3);
    Assert.Equal(interleaved[2] / interleaved[3], 10.0f, 3);
    Assert.All(interleaved, sample => Assert.InRange(Math.Abs(sample), 0.0f, (float)AudioMath.DbToLinear(-1)));
}

[Fact]
public void Process_replaces_non_finite_input_with_silence()
{
    var limiter = new StereoLinkedLimiter(48_000, TestSettings());
    float[] interleaved = [float.NaN, float.PositiveInfinity];
    limiter.Process(interleaved);
    Assert.Equal([0.0f, 0.0f], interleaved);
}
```

- [ ] **Step 2: Run focused DSP tests and verify failure**

Run: `dotnet test tests/EftToolkit.Tests/EftToolkit.Tests.csproj --filter FullyQualifiedName~StereoLinkedLimiter`

Expected: FAIL because the limiter is undefined.

- [ ] **Step 3: Implement the detector and gain computer**

Process interleaved stereo frames only; reject odd sample counts. Convert input gain and ceiling once per configuration. For each amplified frame, use `peak = Max(Abs(left), Abs(right), 1e-12)` and `levelDb = 20 * Log10(peak)`.

Compute compression gain in dB with a soft knee:

```csharp
double over = levelDb - thresholdDb;
double gainDb = over switch
{
    _ when over <= -kneeDb / 2.0 => 0.0,
    _ when over >= kneeDb / 2.0 => (thresholdDb + over / ratio) - levelDb,
    _ => (1.0 / ratio - 1.0) * Math.Pow(over + kneeDb / 2.0, 2.0) / (2.0 * kneeDb)
};
double targetGain = Math.Min(1.0, AudioMath.DbToLinear(gainDb));
```

Special-case `kneeDb=0` to avoid division by zero. Use the attack coefficient when target gain is below the current envelope and release otherwise. Apply one envelope to both delayed channels.

- [ ] **Step 4: Implement look-ahead, hard ceiling, and metering**

Allocate the stereo circular buffer only in the constructor or `Reconfigure`; its frame capacity is `round(sampleRate * lookAheadMs / 1000)`. Push amplified input, detect current input, and output the sample delayed by that capacity. Replace non-finite input with zero. After envelope multiplication, clamp both channels to `±DbToLinear(CeilingDbFs)`.

Publish `record AudioProcessorMetrics(double InputPeakDbFs, double OutputPeakDbFs, double GainReductionDb)` where gain reduction is positive. Use atomic field exchange; do not allocate per audio block.

- [ ] **Step 5: Add deterministic signal tests and commit**

Test silence, impulses, steady sine, release recovery, zero knee, 44.1/48 kHz look-ahead lengths, 30 seconds of generated samples, no per-block managed allocation after warm-up, and exact reset to silence. Run:

`dotnet test tests/EftToolkit.Tests/EftToolkit.Tests.csproj --filter FullyQualifiedName~Audio`

Expected: PASS. Commit:

```bash
git add src/EftToolkit.Audio/Dsp tests/EftToolkit.Tests/Audio
git commit -m "feat: add stereo-linked audio limiter"
```

### Task 8: Audio endpoints, routing validation, and process monitoring

**Files:**
- Create: `src/EftToolkit.Audio/Devices/AudioEndpointDescriptor.cs`
- Create: `src/EftToolkit.Audio/Devices/IAudioDeviceCatalog.cs`
- Create: `src/EftToolkit.Audio/Devices/NAudioDeviceCatalog.cs`
- Create: `src/EftToolkit.Audio/Processes/IProcessMonitor.cs`
- Create: `src/EftToolkit.Audio/Processes/PollingProcessMonitor.cs`
- Create: `src/EftToolkit.Audio/Routing/AudioRoute.cs`
- Create: `src/EftToolkit.Audio/Routing/AudioRouteValidator.cs`
- Test: `tests/EftToolkit.Tests/Audio/AudioRouteValidatorTests.cs`
- Test: `tests/EftToolkit.Tests/Audio/PollingProcessMonitorTests.cs`

**Interfaces:**
- Consumes: `AudioProfileOptions`, `IAppLogger`, and NAudio.Wasapi 2.4.0.
- Produces: endpoint catalog, target-running events, active session process IDs, and a validated three-endpoint route.

- [ ] **Step 1: Write failing route validation tests**

```csharp
public enum AudioDataFlow { Capture, Render }

public sealed record AudioEndpointDescriptor(
    string Id,
    string FriendlyName,
    AudioDataFlow Flow,
    bool IsActive,
    int Channels,
    int SampleRate);

public sealed record AudioRoute(
    string VirtualRenderEndpointId,
    string VirtualCaptureEndpointId,
    string PhysicalRenderEndpointId,
    string ExecutableName);

public sealed record AudioRouteValidation(bool IsValid, AudioRoute? Route, string? ErrorCode);

[Fact]
public void Validate_requires_virtual_render_capture_pair_and_physical_render()
{
    AudioRouteValidation result = AudioRouteValidator.Validate(_profile, _endpoints);
    Assert.True(result.IsValid);
    Assert.Equal(_virtualRender.Id, result.Route!.VirtualRenderEndpointId);
    Assert.Equal(_virtualCapture.Id, result.Route.VirtualCaptureEndpointId);
    Assert.Equal(_headphones.Id, result.Route.PhysicalRenderEndpointId);
}
```

Add negative cases for missing IDs, inactive devices, wrong flows, same virtual and physical render IDs, non-stereo endpoints, and executable names containing directories.

- [ ] **Step 2: Run route tests and verify failure**

Run: `dotnet test tests/EftToolkit.Tests/EftToolkit.Tests.csproj --filter FullyQualifiedName~AudioRouteValidator`

Expected: FAIL because routing types do not exist.

- [ ] **Step 3: Implement catalog and validation**

`NAudioDeviceCatalog` uses `MMDeviceEnumerator.EnumerateAudioEndPoints` for active capture and render devices, reads `AudioClient.MixFormat`, and disposes every temporary `MMDevice`. It provides:

```csharp
public interface IAudioDeviceCatalog : IAsyncDisposable
{
    event EventHandler? DevicesChanged;
    Task<IReadOnlyList<AudioEndpointDescriptor>> GetEndpointsAsync(CancellationToken cancellationToken);
    Task<IReadOnlySet<int>> GetActiveProcessIdsAsync(string renderEndpointId, CancellationToken cancellationToken);
}
```

Enumerate audio sessions only on the configured virtual render endpoint. Skip system-sounds sessions and sessions whose process ID cannot be read; log those failures at Debug. Register `IMMNotificationClient` and raise `DevicesChanged` without opening an audio stream.

Validation accepts only active stereo endpoints at 44.1 or 48 kHz for the first release. Normalize executable names with `Path.GetFileNameWithoutExtension`, and compare ordinal-ignore-case.

- [ ] **Step 4: Write and implement process monitor tests**

```csharp
public interface IProcessMonitor : IAsyncDisposable
{
    bool IsRunning { get; }
    IReadOnlySet<int> ProcessIds { get; }
    event EventHandler? Changed;
    Task StartAsync(string executableName, CancellationToken cancellationToken);
    Task StopAsync(CancellationToken cancellationToken);
}
```

Inject `IProcessSnapshotProvider` in tests. Poll once per second, emit only on a changed PID set, and never call OpenProcess with rights beyond what `System.Diagnostics.Process` uses to obtain ID and process name. Do not inspect modules, handles, memory, or command lines.

- [ ] **Step 5: Run tests, verify no ASIO dependency, and commit**

Run:

```powershell
dotnet test tests/EftToolkit.Tests/EftToolkit.Tests.csproj --filter "FullyQualifiedName~AudioRoute|FullyQualifiedName~ProcessMonitor"
dotnet list src/EftToolkit.Audio/EftToolkit.Audio.csproj package --include-transitive | Select-String -Pattern "NAudio.Asio"
```

Expected: tests PASS and `Select-String` returns no matches. Commit:

```bash
git add src/EftToolkit.Audio tests/EftToolkit.Tests/Audio
git commit -m "feat: discover shared audio routes and target processes"
```

### Task 9: Shared-mode WASAPI capture, conversion, buffering, and render

**Files:**
- Create: `src/EftToolkit.Audio/Streaming/IAudioStreamSession.cs`
- Create: `src/EftToolkit.Audio/Streaming/AudioStreamMetrics.cs`
- Create: `src/EftToolkit.Audio/Streaming/PcmFloatConverter.cs`
- Create: `src/EftToolkit.Audio/Streaming/ProcessedSampleProvider.cs`
- Create: `src/EftToolkit.Audio/Streaming/INAudioClientFactory.cs`
- Create: `src/EftToolkit.Audio/Streaming/NAudioSharedStreamSession.cs`
- Create: `src/EftToolkit.Audio/Properties/AssemblyInfo.cs`
- Test: `tests/EftToolkit.Tests/Audio/PcmFloatConverterTests.cs`
- Test: `tests/EftToolkit.Tests/Audio/ProcessedSampleProviderTests.cs`
- Test: `tests/EftToolkit.Tests/Audio/NAudioSharedStreamSessionTests.cs`

**Interfaces:**
- Consumes: `AudioRoute`, `StereoLinkedLimiter`, `IAppLogger`, and NAudio endpoint IDs.
- Produces: a shared-only stream session with metrics and deterministic stop behavior.

- [ ] **Step 1: Write failing PCM conversion and buffer-underrun tests**

Cover little-endian PCM 16, packed PCM 24, PCM 32, and IEEE float 32 input. Every conversion produces interleaved floats, maps full-scale negative to `-1`, maps maximum positive below or equal to `1`, and replaces non-finite float input with zero.

Test that an empty `ProcessedSampleProvider.Read` fills the requested output with silence and increments underruns; overflow drops the newest incoming block, never replays old samples, and increments overruns.

- [ ] **Step 2: Run focused tests and verify failure**

Run: `dotnet test tests/EftToolkit.Tests/EftToolkit.Tests.csproj --filter "FullyQualifiedName~PcmFloatConverter|FullyQualifiedName~ProcessedSampleProvider"`

Expected: FAIL because conversion and provider types do not exist.

- [ ] **Step 3: Implement conversion and the bounded provider**

`PcmFloatConverter.ConvertToFloat(ReadOnlySpan<byte>, WaveFormat, Span<float>)` returns samples written and rejects non-stereo formats. `ProcessedSampleProvider` owns a bounded ring sized for 100 ms at the negotiated sample rate. Capture callbacks convert and process directly into pooled buffers, then enqueue. Render pulls from the ring. Return pooled buffers in `finally`; do not allocate in steady-state callbacks.

Expose:

```csharp
public sealed record AudioStreamMetrics(
    long Underruns,
    long Overruns,
    int CaptureBufferMilliseconds,
    int RenderBufferMilliseconds,
    double EstimatedAdditionalLatencyMilliseconds,
    AudioProcessorMetrics Processor);

public interface IAudioStreamSession : IAsyncDisposable
{
    bool IsRunning { get; }
    AudioStreamMetrics Metrics { get; }
    event EventHandler<Exception>? Faulted;
    Task StartAsync(AudioRoute route, AudioLimiterOptions limiter, CancellationToken cancellationToken);
    Task SetBypassAsync(bool bypass, CancellationToken cancellationToken);
    Task StopAsync(CancellationToken cancellationToken);
}
```

- [ ] **Step 4: Write failing fake-backend lifecycle tests**

Inject an `INAudioClientFactory` so tests do not touch hardware. Verify Start opens exactly one capture and one render client, both with `AudioClientShareMode.Shared`; initialization failure disposes both sides; duplicate Start is rejected; repeated Stop is safe; and Faulted fires once.

The internal seam has this exact shape; production implements it with NAudio and tests provide recording clients:

```csharp
internal interface INAudioClientFactory
{
    ISharedCaptureClient CreateCapture(string endpointId);
    ISharedRenderClient CreateRender(string endpointId, int latencyMilliseconds);
}

internal interface ISharedCaptureClient : IAsyncDisposable
{
    WaveFormat WaveFormat { get; }
    event EventHandler<CapturedAudioEventArgs>? DataAvailable;
    event EventHandler<Exception>? Faulted;
    Task StartAsync(CancellationToken cancellationToken);
    Task StopAsync(CancellationToken cancellationToken);
}

internal interface ISharedRenderClient : IAsyncDisposable
{
    WaveFormat MixFormat { get; }
    event EventHandler<Exception>? Faulted;
    Task StartAsync(IWaveProvider provider, AudioClientShareMode shareMode, CancellationToken cancellationToken);
    Task StopAsync(CancellationToken cancellationToken);
}

internal sealed record CapturedAudioEventArgs(ReadOnlyMemory<byte> Buffer, int BytesRecorded);
```

Expose these internal seams to the test assembly only:

```csharp
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("EftToolkit.Tests")]
```

- [ ] **Step 5: Implement NAudio shared streaming**

Resolve devices by exact endpoint ID. Use `WasapiCapture` for the virtual capture endpoint and `WasapiOut(renderDevice, AudioClientShareMode.Shared, useEventSync: true, latency: 20)` for physical output. Reject capture or render formats with channels other than two. If sample rates differ, wrap the processed provider in `MediaFoundationResampler` targeting the render mix format with quality 60; both rates must remain 44.1 or 48 kHz.

On stop, linearly ramp processed gain to zero over 10 ms, stop capture before render, detach events, dispose capture, provider/resampler, render, and MMDevice instances in that order. Enforce a three-second stop timeout and log any timed-out component.

Bypass keeps both streams open and changes processing to unity gain with only finite-value sanitation; it does not disable forwarding.

- [ ] **Step 6: Run automated and opt-in audio smoke tests**

Run: `dotnet test tests/EftToolkit.Tests/EftToolkit.Tests.csproj --filter FullyQualifiedName~Audio`

Expected: PASS.

Add a `Category=Hardware` smoke test gated by `EFT_TOOLKIT_HARDWARE_TESTS=1`, `EFT_TOOLKIT_VIRTUAL_CAPTURE_ID`, and `EFT_TOOLKIT_PHYSICAL_RENDER_ID`. It starts shared streaming for five seconds and asserts no fault. It must never choose an endpoint by index or open ASIO.

- [ ] **Step 7: Commit**

```bash
git add src/EftToolkit.Audio/Streaming tests/EftToolkit.Tests/Audio
git commit -m "feat: stream processed audio through shared WASAPI"
```

### Task 10: Audio module state machine and device/process recovery

**Files:**
- Create: `src/EftToolkit.Audio/AudioModuleStatus.cs`
- Create: `src/EftToolkit.Audio/AudioModule.cs`
- Test: `tests/EftToolkit.Tests/Audio/AudioModuleTests.cs`

**Interfaces:**
- Consumes: `IToolkitModule`, active `AudioProfileOptions`, device catalog, process monitor, route validator, stream session, options store, and logger.
- Produces: independent audio state, target/session warnings, retry behavior, bypass, and live metrics.

- [ ] **Step 1: Write failing state transition tests**

Test this matrix:

```text
Enable + missing endpoints     -> Faulted / MissingDependency, no stream opened
Enable + target absent         -> Degraded / WaitingForTarget, no stream opened
Target appears + valid route   -> Active, shared stream opened once
Set bypass                     -> Bypass, stream remains open
Relevant device disappears    -> Degraded, stream stopped and disposed
Device notification restores  -> Active after one debounced retry
Target exits                   -> Degraded / WaitingForTarget, stream stopped
Disable from any state         -> Disabled, all subscriptions removed
```

Also test that a second active process on the virtual render endpoint produces a warning without stopping audio.

- [ ] **Step 2: Run module tests and verify failure**

Run: `dotnet test tests/EftToolkit.Tests/EftToolkit.Tests.csproj --filter FullyQualifiedName~AudioModuleTests`

Expected: FAIL because AudioModule is undefined.

- [ ] **Step 3: Implement serialized state transitions**

Use one `SemaphoreSlim` transition gate. Never hold it while raising public events. Device and process events queue reevaluation through a bounded latest-value channel with 500 ms debounce. Reevaluate only on relevant endpoint IDs or target PID changes; do not spin on a timer when an endpoint is missing. When `EstimatedAdditionalLatencyMilliseconds` exceeds 50, keep processing but expose warning code `LatencyTargetExceeded` with the measured value.

```csharp
public sealed class AudioModule : IAudioController
{
    public AudioModuleStatus Detail { get; }
    public AudioStreamMetrics? Metrics { get; }
    public bool IsTargetRunning { get; }
    public bool HasUnrelatedSessions { get; }
    public Task SetBypassAsync(bool bypass, CancellationToken cancellationToken);
    public Task RetryAsync(CancellationToken cancellationToken);
}
```

Define `AudioModuleStatus` as `record AudioModuleStatus(ModuleStatus Module, bool IsTargetRunning, bool IsRouteOpen, bool HasUnrelatedSessions, string? WarningCode, string? WarningMessage)` so App never parses display strings to determine behavior.

Map all expected exceptions to stable error codes: `MissingVirtualRender`, `MissingVirtualCapture`, `MissingPhysicalRender`, `UnsupportedFormat`, `SharedModeOpenFailed`, `StreamFaulted`, and `TargetNotRunning`. Preserve native HResult in logs, not in user-facing copy.

- [ ] **Step 4: Add idempotence and concurrency tests**

Issue concurrent Enable/Disable/Retry calls with controlled fakes. Assert one active stream maximum, exactly-once disposal, no events after disable, and final Disabled state.

- [ ] **Step 5: Run tests and commit**

Run: `dotnet test tests/EftToolkit.Tests/EftToolkit.Tests.csproj --filter FullyQualifiedName~Audio`

Expected: PASS. Commit:

```bash
git add src/EftToolkit.Audio tests/EftToolkit.Tests/Audio
git commit -m "feat: orchestrate optional audio enhancement"
```

### Task 11: Single-instance activation and ordered application lifecycle

**Files:**
- Create: `src/EftToolkit.Platform.Windows/SingleInstance/ISingleInstanceGate.cs`
- Create: `src/EftToolkit.Platform.Windows/SingleInstance/NamedPipeSingleInstanceGate.cs`
- Create: `src/EftToolkit.Core/Lifecycle/ToolkitCoordinator.cs`
- Create: `src/EftToolkit.Core/Lifecycle/ShutdownResult.cs`
- Test: `tests/EftToolkit.Tests/Platform/NamedPipeSingleInstanceGateTests.cs`
- Test: `tests/EftToolkit.Tests/Core/ToolkitCoordinatorTests.cs`

**Interfaces:**
- Consumes: `IDisplayController`, `IAudioController`, `IHotkeyService`, `IPlatformEventSource`, `IOptionsStore`, and logger, all through Core-owned contracts.
- Produces: primary-instance activation events and one ordered, finite shutdown path.

- [ ] **Step 1: Write failing single-instance tests**

Use a unique test suffix. Assert that the first gate is primary, the second is secondary, `NotifyPrimaryAsync` causes one `ActivationRequested` event, and disposing the primary lets a new gate become primary.

```csharp
public interface ISingleInstanceGate : IAsyncDisposable
{
    bool IsPrimary { get; }
    event EventHandler? ActivationRequested;
    Task<bool> AcquireAsync(CancellationToken cancellationToken);
    Task NotifyPrimaryAsync(CancellationToken cancellationToken);
}
```

- [ ] **Step 2: Implement the gate securely**

Use a named mutex and named pipe suffixed with the current Windows user SID. Create the server with `PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly`. The secondary writes one UTF-8 line `activate` and exits. The server treats every other payload as invalid and logs it.

- [ ] **Step 3: Write failing lifecycle order tests**

With recording fakes, assert the exact clean-shutdown order:

```text
reject-commands
audio-bypass
audio-disable
display-disable-and-restore
hotkeys-unregister
platform-events-stop
settings-save
logger-flush-and-dispose
```

Assert repeated shutdown returns the same task/result and each participant runs once. Assert a failure in one step is recorded and remaining cleanup continues.

- [ ] **Step 4: Implement ToolkitCoordinator**

Use a single cached shutdown task and a linked cancellation token with a ten-second total timeout. `StartAsync` loads and validates options, runs display crash recovery before enabling either module, starts platform events, and then enables only configured modules. When Display is configured on, enable it and register hotkeys; when it is configured off, do neither. Platform display events call `RefreshAndReapplyAsync` only while Display is enabled; hotkey events call `ApplyPresetAsync` without blocking the native callback.

Expose `SetDisplayEnabledAsync(bool, CancellationToken)` and `SetAudioEnabledAsync(bool, CancellationToken)` on the coordinator. Display enable starts the module and then registers F2-F5. Display disable first unregisters F2-F5 and then restores/disables the module. Audio enable/disable touches no Display service. Persist the new enabled flag only after the requested transition succeeds.

`ShutdownResult` contains start/end UTC, timed-out flag, and a list of named failures. Persist final settings even when a prior cleanup step failed.

- [ ] **Step 5: Run tests and commit**

Run: `dotnet test tests/EftToolkit.Tests/EftToolkit.Tests.csproj --filter "FullyQualifiedName~SingleInstance|FullyQualifiedName~ToolkitCoordinator"`

Expected: PASS. Commit:

```bash
git add src/EftToolkit.Core/Lifecycle src/EftToolkit.Platform.Windows/SingleInstance tests/EftToolkit.Tests
git commit -m "feat: coordinate single-instance lifecycle"
```

### Task 12: WPF panel, tray behavior, and composition root

**Files:**
- Modify: `src/EftToolkit.App/App.xaml`
- Modify: `src/EftToolkit.App/App.xaml.cs`
- Modify: `src/EftToolkit.App/MainWindow.xaml`
- Modify: `src/EftToolkit.App/MainWindow.xaml.cs`
- Create: `src/EftToolkit.App/ViewModels/MainViewModel.cs`
- Create: `src/EftToolkit.App/ViewModels/DisplayViewModel.cs`
- Create: `src/EftToolkit.App/ViewModels/AudioViewModel.cs`
- Create: `src/EftToolkit.App/ViewModels/DisplayRowViewModel.cs`
- Create: `src/EftToolkit.App/ViewModels/AudioEndpointViewModel.cs`
- Create: `src/EftToolkit.App/Commands/AsyncRelayCommand.cs`
- Create: `src/EftToolkit.App/Lifecycle/WindowClosePolicy.cs`
- Create: `src/EftToolkit.App/Tray/NotifyIconHost.cs`
- Create: `src/EftToolkit.App/Dialogs/IUserDialogService.cs`
- Create: `src/EftToolkit.App/Dialogs/WpfUserDialogService.cs`
- Test: `tests/EftToolkit.Tests/App/MainViewModelTests.cs`
- Test: `tests/EftToolkit.Tests/App/WindowClosePolicyTests.cs`

**Interfaces:**
- Consumes: all module states, settings, lifecycle coordinator, single-instance activation, and logs.
- Produces: the approved one-window panel, tray-only close, explicit exit confirmation, and composition root.

- [ ] **Step 1: Write failing view-model and close-policy tests**

Test that Display and Audio toggles call the coordinator's corresponding enable method without touching the other module; selecting multiple monitors persists stable IDs; preset fields validate approved ranges; audio meters update at no more than 10 Hz; missing dependencies do not disable Display controls; and Close returns Hide unless `IsShuttingDown=true`.

```csharp
public static class WindowClosePolicy
{
    public static WindowCloseAction Decide(bool isShuttingDown) =>
        isShuttingDown ? WindowCloseAction.Close : WindowCloseAction.Hide;
}

public enum WindowCloseAction { Hide, Close }
```

- [ ] **Step 2: Run App tests and verify failure**

Run: `dotnet test tests/EftToolkit.Tests/EftToolkit.Tests.csproj --filter FullyQualifiedName~EftToolkit.Tests.App`

Expected: FAIL because the App view models and close policy do not exist.

- [ ] **Step 3: Implement the panel layout and bindings**

Use one scrollable window between 720 and 900 device-independent pixels wide. The header shows overall status and “关闭窗口后将在系统托盘继续运行”. The Display group contains an enable toggle, checkable display rows, HDR experimental badges, F2-F5 current state, editable Low/Medium/High gamma/lift/ceiling fields, and per-display result text.

The Audio group contains enable and bypass toggles, profile/executable selection, virtual render/capture and physical render selectors, gain/threshold/ceiling fields, input/output/gain-reduction meters, shared-mode label, target state, and dependency/routing warnings. Include “打开 Windows 音量混合器” using `ms-settings:apps-volume` and explain that routing is manual.

The footer shows the latest warning and opens `%LOCALAPPDATA%\EftToolkit\logs` with `ProcessStartInfo.UseShellExecute=true`.

- [ ] **Step 4: Implement tray and close interception**

Use `System.Windows.Forms.NotifyIcon` owned by `NotifyIconHost`. Menu items are Open and Exit. MainWindow `Closing` applies `WindowClosePolicy`; Hide cancels the close and calls `Hide()`. Double-clicking the icon or receiving single-instance activation dispatches `Show`, `WindowState=Normal`, and `Activate` on the WPF dispatcher.

Exit calls the dialog service when Audio reports both `IsTargetRunning` and an open route. The Chinese confirmation states that exiting stops Cable forwarding and the target must be routed back to the physical device. Cancel leaves everything running. Confirm sets `IsShuttingDown`, awaits coordinator shutdown, disposes the tray icon, and closes the window.

- [ ] **Step 5: Compose services and handle fatal exceptions**

`App.OnStartup` acquires the single-instance gate before creating hardware services. A secondary instance notifies primary and calls `Shutdown(0)`. The primary constructs stores/logger, gateways, modules, message sink, coordinator, view models, window, and tray host.

Handle `DispatcherUnhandledException`, `AppDomain.CurrentDomain.UnhandledException`, and `TaskScheduler.UnobservedTaskException`. Log each and request the cached coordinator shutdown; do not claim cleanup succeeded if the process is terminating.

- [ ] **Step 6: Run tests, launch manually, and commit**

Run:

```powershell
dotnet test tests/EftToolkit.Tests/EftToolkit.Tests.csproj
dotnet run --project src/EftToolkit.App/EftToolkit.App.csproj
```

Manual expected result: the panel opens, X hides it, the tray icon restores it, and a second launch activates the first instance. Commit:

```bash
git add src/EftToolkit.App tests/EftToolkit.Tests/App
git commit -m "feat: add EFT Toolkit panel and tray lifecycle"
```

### Task 13: End-to-end recovery tests, packaging, CI, and operator documentation

**Files:**
- Create: `tests/EftToolkit.Tests/Integration/ShutdownRecoveryTests.cs`
- Create: `tests/EftToolkit.Tests/Integration/ModuleIsolationTests.cs`
- Create: `scripts/publish.ps1`
- Create: `.github/workflows/windows.yml`
- Create: `docs/manual-test-checklist.md`
- Modify: `README.md`

**Interfaces:**
- Consumes: the complete application.
- Produces: a repeatable release artifact and an auditable Windows 11 acceptance process.

- [ ] **Step 1: Write failing cross-module integration tests with fakes**

Test that an Audio dependency fault leaves Display active; a Display write fault leaves Audio active; window-hide does not call shutdown; tray Exit restores original ramps before disposing hotkeys; and a simulated unclean prior run restores only a fingerprint-matching ramp.

Run: `dotnet test tests/EftToolkit.Tests/EftToolkit.Tests.csproj --filter FullyQualifiedName~Integration`

Expected: FAIL if any cross-module wiring or shutdown seam is missing.

- [ ] **Step 2: Correct integration seams minimally and run the full suite**

Change only composition or contracts required by the failing integration tests. Do not add new product features. Run:

`dotnet test EftToolkit.sln --configuration Release --logger "trx;LogFileName=eft-toolkit-tests.trx"`

Expected: PASS with a TRX file and zero skipped non-hardware tests.

- [ ] **Step 3: Add deterministic publishing**

Create `scripts/publish.ps1` with terminating errors and this publish command:

```powershell
$ErrorActionPreference = 'Stop'
$output = Join-Path $PSScriptRoot '..\artifacts\publish\win-x64'
if (Test-Path $output) { Remove-Item $output -Recurse -Force }
dotnet publish (Join-Path $PSScriptRoot '..\src\EftToolkit.App\EftToolkit.App.csproj') `
  --configuration Release `
  --runtime win-x64 `
  --self-contained true `
  --output $output `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:DebugType=embedded
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }
Copy-Item (Join-Path $PSScriptRoot '..\LICENSE') $output
Copy-Item (Join-Path $PSScriptRoot '..\README.md') $output
```

Do not package VB-CABLE. Verify the publish directory has `EftToolkit.App.exe`, license, README, and no file whose name contains `asio`.

- [ ] **Step 4: Add Windows CI**

Use `actions/checkout@v4` and `actions/setup-dotnet@v4` with SDK `8.0.x`. On `windows-latest`, run restore, Release build with warnings as errors, all non-hardware tests, `scripts/publish.ps1`, an ASIO filename/dependency rejection check, and upload `artifacts/publish/win-x64` with `actions/upload-artifact@v4`.

The rejection check fails if either dependency or published filename contains ASIO:

```powershell
$dependencyMatches = dotnet list src/EftToolkit.Audio/EftToolkit.Audio.csproj package --include-transitive | Select-String -Pattern 'NAudio.Asio'
$fileMatches = Get-ChildItem artifacts/publish/win-x64 -Recurse | Where-Object Name -Match 'asio'
if ($dependencyMatches -or $fileMatches) { throw 'ASIO dependency or artifact detected' }
```

- [ ] **Step 5: Write README and the manual acceptance checklist**

README must state Windows 11 x64, no injection/hooking, no administrator requirement, optional VB-CABLE-style dependency, manual Windows routing, shared-mode-only audio, no ASIO access, F2-F5 behavior, HDR experimental status, tray close behavior, hearing-safety disclaimer, build command, test command, and publish command.

`docs/manual-test-checklist.md` must provide pass/fail fields for:

- One display and two displays switching F2-F5 and exact F2 restoration.
- Per-display failure visibility.
- SDR and HDR write/readback observation.
- Disconnect/reconnect, resolution change, lock/unlock, sleep/resume.
- Full-screen EFT hotkeys without injection or hooks.
- Missing Cable, Cable disconnect, physical endpoint disconnect, and default-device change.
- Only explicitly routed programs being enhanced; unrelated Cable sessions warning.
- A concurrently running ASIO client remaining uninterrupted.
- 30-minute 48 kHz playback with underrun/overrun, memory, and handle observations.
- Measured additional latency at or below 50 ms, or a visible warning when above it.
- X hiding to tray, second-instance activation, canceled Exit, confirmed Exit, and original-ramp restoration.

- [ ] **Step 6: Run final verification**

On Windows 11 x64 run:

```powershell
dotnet restore EftToolkit.sln
dotnet build EftToolkit.sln --configuration Release -warnaserror --no-restore
dotnet test EftToolkit.sln --configuration Release --no-build --filter 'Category!=Hardware'
pwsh -File scripts/publish.ps1
dotnet list src/EftToolkit.Audio/EftToolkit.Audio.csproj package --include-transitive
```

Expected: restore/build/test/publish succeed; dependency output contains `NAudio.Wasapi 2.4.0` and contains neither `NAudio` meta-package nor `NAudio.Asio`. Then complete the manual checklist on representative Windows 11 hardware before calling the release accepted.

- [ ] **Step 7: Commit the release infrastructure**

```bash
git add tests/EftToolkit.Tests/Integration scripts .github README.md docs/manual-test-checklist.md
git commit -m "chore: add Windows verification and release workflow"
```

## Execution Notes for the Implementing Agent

1. Read the linked spec and this entire plan before editing files.
2. Create an isolated worktree with `superpowers:using-git-worktrees` before implementation.
3. Use `superpowers:subagent-driven-development` when tasks are delegated in one session, or `superpowers:executing-plans` for checkpointed inline execution.
4. Do not parallelize tasks that consume interfaces from an unfinished earlier task. Tasks 3 and 7 may run independently after Tasks 1-2; Tasks 4-6 depend on Task 3; Tasks 8-10 depend on Task 7.
5. Keep every task on its own commit. If a test reveals an architectural contradiction, stop and update the design and plan before broadening scope.
6. Never claim Windows, display, audio, latency, ASIO coexistence, or packaging success from the Linux planning host. Those claims require the Windows verification commands and manual checklist above.
