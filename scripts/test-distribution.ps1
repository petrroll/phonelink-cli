[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$root = Split-Path $PSScriptRoot -Parent
$temp = Join-Path ([IO.Path]::GetTempPath()) ('phonelink-publish-policy-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temp | Out-Null
try {
    # A text placeholder deliberately named .dll: never copy a real vendor DLL.
    $fixture = Join-Path $temp 'placeholder.dll'
    Set-Content -LiteralPath $fixture -Value 'Original synthetic distribution test; not an assembly.' -Encoding ascii
    foreach ($singleFile in @('true', 'false')) {
        $arguments = @(
            'publish', (Join-Path $root 'src\PhoneLink.Cli\PhoneLink.Cli.csproj'),
            '-c', 'Release', '-r', 'win-x64', '--self-contained', 'false',
            "-p:PublishSingleFile=$singleFile",
            "-p:CustomAfterMicrosoftCommonTargets=$(Join-Path $root 'tests\Distribution\InjectUnexpected.targets')",
            "-p:DistributionTestFile=$fixture", '-o', (Join-Path $temp "out-$singleFile")
        )
        $output = & dotnet @arguments 2>&1 | Out-String
        if ($LASTEXITCODE -eq 0 -or $output -notmatch 'PLINK001') {
            throw "Publish guard did not reject the synthetic extra DLL (single-file=$singleFile): $output"
        }
        Write-Host "PASS unapproved publish input rejected (single-file=$singleFile)"
    }
}
finally { Remove-Item -LiteralPath $temp -Recurse -Force }
$global:LASTEXITCODE = 0 # Expected negative publishes must not fail the outer CI shell.
