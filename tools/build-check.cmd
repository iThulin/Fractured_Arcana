@echo off
REM ============================================================
REM  build-check.cmd  -  compile-only verification for Fractured Arcana
REM
REM  Requires: .NET 8 SDK  (winget install Microsoft.DotNet.SDK.8)
REM  Does NOT require the Godot editor. Godot.NET.Sdk and GodotSharp
REM  restore from NuGet, so this surfaces real C# compile errors.
REM
REM  Writes build.log to the repo root. That folder is mounted into
REM  Cowork sessions, so an agent can read the log without any
REM  copy-paste. build.log is gitignored.
REM ============================================================
setlocal
cd /d "%~dp0.."

where dotnet >nul 2>&1
if errorlevel 1 (
  echo [build-check] dotnet not found on PATH.
  echo [build-check] Install it with:  winget install Microsoft.DotNet.SDK.8
  echo [build-check] Then open a NEW terminal and run this again.
  pause
  exit /b 9009
)

echo [build-check] dotnet --version:
dotnet --version

echo [build-check] building FracturedArcana.csproj ...
dotnet build FracturedArcana.csproj -c Debug -v minimal --nologo > build.log 2>&1
set RC=%ERRORLEVEL%
echo. >> build.log
echo === exit code: %RC% === >> build.log

type build.log
echo.
if "%RC%"=="0" (echo [build-check] BUILD OK) else (echo [build-check] BUILD FAILED - see build.log)
pause
exit /b %RC%
