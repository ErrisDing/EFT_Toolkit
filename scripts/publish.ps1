#Requires -Version 7.0
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

    Terminating: a failed step stops the script, so an artifact that was not fully built can never be
    uploaded or handed to anyone.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

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
    throw "The publish directory contains files that look like ASIO components: $($asioFiles.Name -join ', ')"
}

foreach ($required in @('EftToolkit.App.exe', 'LICENSE', 'README.md')) {
    if (-not (Test-Path (Join-Path $output $required))) {
        throw "The publish directory is missing $required."
    }
}

Write-Host "Published to $output"
Write-Host 'Not included, by design: any virtual audio driver, any ASIO component, and any installer.'
