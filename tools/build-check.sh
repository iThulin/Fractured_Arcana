#!/usr/bin/env bash
# ============================================================
#  build-check.sh  -  compile-only verification (macOS / Linux)
#  Mirror of tools/build-check.cmd. See that file for rationale.
#
#  Requires: .NET SDK 8 or newer  (brew install --cask dotnet-sdk)
#  Does NOT require the Godot editor — Godot.NET.Sdk and GodotSharp
#  restore from NuGet, so this surfaces real C# compile errors.
#
#  Writes build.log to the repo root, which is the folder mounted
#  into Cowork sessions — an agent can then read the compiler output
#  with no copy-paste. build.log is gitignored.
# ============================================================
set -uo pipefail
cd "$(dirname "$0")/.." || exit 1

if ! command -v dotnet >/dev/null 2>&1; then
  echo "[build-check] dotnet not found on PATH."
  echo "[build-check] Install it with:  brew install --cask dotnet-sdk"
  echo "[build-check] Then open a NEW terminal and run this again."
  exit 127
fi

echo "[build-check] dotnet --version:"
dotnet --version

echo "[build-check] building FracturedArcana.csproj ..."
dotnet build FracturedArcana.csproj -c Debug -v minimal --nologo > build.log 2>&1
RC=$?
{ echo; echo "=== exit code: $RC ==="; } >> build.log

cat build.log
echo
if [ "$RC" -eq 0 ]; then echo "[build-check] BUILD OK"; else echo "[build-check] BUILD FAILED — see build.log"; fi
exit $RC
