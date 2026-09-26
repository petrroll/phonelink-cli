[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
& (Join-Path $PSScriptRoot 'build.ps1')
$testDirectory = Join-Path $root 'dist\tests'
& dotnet publish (Join-Path $root 'tests\PhoneLink.Tests\PhoneLink.Tests.csproj') -c Release -r win-x64 --self-contained false -p:PublishSingleFile=false -o $testDirectory
if ($LASTEXITCODE -ne 0) { throw 'Test publish failed.' }
& (Join-Path $testDirectory 'PhoneLink.Tests.exe') (Join-Path $root 'dist\win-x64\phonelink.exe')
if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
