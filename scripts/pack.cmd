@echo off
REM One-click packaging: double-click this, or run it from any directory.
REM
REM It only starts the PowerShell script next to it. Everything the artifact has to satisfy - the
REM self-contained single-file publish, the license copy, and the checks that catch an ASIO
REM component or a missing file - lives there rather than here, so there is one place to read and
REM one place to change. A .cmd wrapper and not a second implementation: a batch copy of those
REM checks could disagree with the real ones, and a batch glob silently reports success when it
REM matches nothing.
setlocal

set "SCRIPTS=%~dp0"
set "ROOT=%SCRIPTS%.."
for %%I in ("%ROOT%") do set "ROOT=%%~fI"

REM Prefer pwsh when it is installed, and fall back to the Windows PowerShell every Windows 11 has.
REM publish.ps1 declares no minimum version for exactly this reason.
where pwsh.exe >nul 2>&1
if %ERRORLEVEL% equ 0 (
    set "POWERSHELL=pwsh.exe"
) else (
    set "POWERSHELL=powershell.exe"
)

echo Packaging EFT Toolkit with %POWERSHELL%
echo.

"%POWERSHELL%" -NoProfile -File "%SCRIPTS%publish.ps1"
set "EXITCODE=%ERRORLEVEL%"

if not "%EXITCODE%"=="0" (
    echo.
    echo Packaging failed with exit code %EXITCODE%.
    echo The artifact directory was left as it was built; do not hand it to anyone.
    exit /b %EXITCODE%
)

echo.
echo Packaged. The executable is:
echo     %ROOT%\artifacts\publish\win-x64\EftToolkit.App.exe
echo.
echo Self-contained: the machine it runs on does not need .NET installed.

REM Only when the user double-clicked, so a run from an existing console does not open a second one.
REM Redirecting into a command rather than using an `if not defined` guard, because running under a
REM debugger leaves CMDCMDLINE pointing at the debugger and the guard would never fire.
REM find.exe by full path: an installation with a Unix toolchain on PATH resolves a bare `find` to
REM the wrong program, and a failed `find` sets errorlevel 1, so the window would never stay open.
echo %CMDCMDLINE% | "%SystemRoot%\System32\find.exe" /i "%~nx0" >nul
if %ERRORLEVEL% equ 0 (
    pause
)
