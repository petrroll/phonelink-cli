[CmdletBinding()]
param([string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $root 'dist\win-x64' }
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw 'Install the .NET 10 SDK to build.' }
& dotnet publish (Join-Path $root 'src\PhoneLink.Cli\PhoneLink.Cli.csproj') -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o $OutputDirectory
if ($LASTEXITCODE -ne 0) { throw 'CLI publish failed. The .NET 10 SDK is required.' }
Write-Host "Built: $(Join-Path $OutputDirectory 'phonelink.exe')"
