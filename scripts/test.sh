#!/usr/bin/env bash
set -euo pipefail
root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
if ! command -v wslpath >/dev/null 2>&1; then
  printf 'These tests require Windows. Use test.ps1 on Windows, or this script in WSL with interop.\n' >&2
  exit 1
fi
"$root/scripts/build.sh"
if command -v dotnet >/dev/null 2>&1; then sdk="$(command -v dotnet)"; else sdk="$root/.tools/dotnet/dotnet"; fi
"$sdk" publish "$root/tests/PhoneLink.Tests/PhoneLink.Tests.csproj" -c Release -r win-x64 --self-contained false -p:PublishSingleFile=false -o "$root/dist/tests"
chmod +x "$root/dist/tests/PhoneLink.Tests.exe"
"$root/dist/tests/PhoneLink.Tests.exe" "$(wslpath -w "$root/dist/win-x64/phonelink.exe")"
