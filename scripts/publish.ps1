# Requires nothing beyond PowerShell 5.1, which is what a stock Windows 11 has. The build machines
# run this under pwsh 7 and it behaves identically there; the point of the lower floor is that the
# packaging checks can also be run by hand on the machine the app is being tested on.
#
# Deliberately no `#Requires -Version 7.0` and no ternaries: that combination is what made this
# script unrunnable on the development host, so the checks at the bottom were the only ones nobody
# could see fail.
#
# A .ps1 over a .cmd because the checks below are what make the artifact trustworthy, and batch has
# no way to write them: a `*` returns immediately from a FOR without running a single iteration, so
# every check written that way passes regardless of what was published.
<#
.SYNOPSIS
    Builds the release artifact for Windows 11 x64.

.DESCRIPTION
    Publishes the panel as a self-contained single file, copies the license and the README next to
    it, and then checks what it produced rather than trusting the build.

    Nothing is bundled with the application. The audio enhancement needs a virtual audio device that
    the user installs and configures themselves - this script never ships one, and the check at the
    end fails if a component that looks like ASIO ever appears in the output, because the toolkit
    must not carry, initialize, or depend on ASIO at all.

    Terminating on both current and future PowerShell versions: a failed step stops the script, so an
    artifact that was not fully built can never be uploaded or handed to anyone. PowerShell 7 reads
    $PSNativeCommandUseErrorActionPreference and also stops on a non-zero exit from dotnet; 5.1 does
    not, so the exit code is checked explicitly below.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$project = Join-Path $repositoryRoot 'src\EftToolkit.App\EftToolkit.App.csproj'
$output = Join-Path $repositoryRoot 'artifacts\publish\win-x64'

if (Test-Path $output) {
    Write-Host "Removing the previous publish directory: $output"
    Remove-Item $output -Recurse -Force
}

Write-Host "Publishing $project"
dotnet publish $project `
    --configuration Release `
    --runtime win-x64 `
    --self-contained true `
    --output $output `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:DebugType=embedded

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE"
}

Copy-Item (Join-Path $repositoryRoot 'LICENSE') $output
Copy-Item (Join-Path $repositoryRoot 'README.md') $output

# The one thing the artifact must never contain. Matching on the name rather than on a list of
# known files, so a future dependency cannot bring one in unnoticed.
$asioFiles = @(Get-ChildItem $output -Recurse -File | Where-Object { $_.Name -match 'asio' })

if ($asioFiles.Count -gt 0) {
    throw "The publish directory contains files that look like ASIO components: $(($asioFiles | ForEach-Object { $_.Name }) -join ', ')"
}

foreach ($required in @('EftToolkit.App.exe', 'LICENSE', 'README.md')) {
    if (-not (Test-Path (Join-Path $output $required))) {
        throw "The publish directory is missing $required."
    }
}

# Reported rather than asserted, because the size depends on the runtime pack for the machine's
# architecture and on how much NAudio contributes. It prints so a build that suddenly lost a
# dependency is visible; only the checks above decide whether the artifact is acceptable.
$artifact = Get-Item (Join-Path $output 'EftToolkit.App.exe')

Write-Host ("Published to {0}" -f $output)
Write-Host ("  EftToolkit.App.exe: {0:N0} bytes" -f $artifact.Length)
Write-Host ("  Files in the publish directory: {0}" -f (Get-ChildItem $output -Recurse -File).Count)
Write-Host 'Not included, by design: any virtual audio driver, any ASIO component, and any installer.'
