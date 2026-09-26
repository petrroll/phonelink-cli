#!/usr/bin/env bash
set -euo pipefail
root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
if command -v dotnet >/dev/null 2>&1; then
  sdk="$(command -v dotnet)"
elif [[ -x "$root/.tools/dotnet/dotnet" ]]; then
  sdk="$root/.tools/dotnet/dotnet"
else
  printf 'Install the .NET 10 SDK to build.\n' >&2
  exit 1
fi
out="${1:-$root/dist/win-x64}"
"$sdk" publish "$root/src/PhoneLink.Cli/PhoneLink.Cli.csproj" -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o "$out"
chmod +x "$out/phonelink.exe"
printf 'Built: %s/phonelink.exe\n' "$out"
