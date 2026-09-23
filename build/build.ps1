<#
.SYNOPSIS
    Builds, tests and packages Logzilla.

.DESCRIPTION
    Everything here runs on the .NET SDK alone. There is no Inno Setup, no WiX
    and nothing else to install: the installer is a project in this solution.

    The application is published self-contained, so the machine it lands on does
    not need the .NET runtime either. Pass -FrameworkDependent for a much smaller
    build if you know the runtime is already present.

.PARAMETER Configuration
    Debug or Release. Defaults to Release.

.PARAMETER Runtime
    Runtime identifier to publish for. Defaults to win-x64.

.PARAMETER SkipTests
    Skip the unit tests.

.PARAMETER FrameworkDependent
    Publish against an installed .NET runtime instead of bundling one.

.EXAMPLE
    .\build\build.ps1
    .\build\build.ps1 -Runtime win-arm64
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [string]$Runtime = 'win-x64',
    [switch]$SkipTests,
    [switch]$FrameworkDependent
)

$ErrorActionPreference = 'Stop'
$root    = Split-Path -Parent $PSScriptRoot
$publish = Join-Path $root 'artifacts\publish'
$dist    = Join-Path $root 'dist'

function Invoke-Step {
    param([string]$Name, [scriptblock]$Action)
    Write-Host "==> $Name" -ForegroundColor Cyan
    & $Action
    if ($LASTEXITCODE -ne 0) { throw "$Name failed with exit code $LASTEXITCODE" }
}

Push-Location $root
try {
    Invoke-Step 'Building' {
        dotnet build Logzilla.sln -c $Configuration --nologo
    }

    if (-not $SkipTests) {
        Invoke-Step 'Testing' {
            dotnet test Logzilla.sln -c $Configuration --nologo --no-build
        }
    }

    if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }

    $selfContained = (-not $FrameworkDependent).ToString().ToLowerInvariant()
    Invoke-Step "Publishing the app ($Runtime, self-contained=$selfContained)" {
        dotnet publish src\Logzilla.App\Logzilla.App.csproj `
            -c $Configuration -r $Runtime --self-contained $selfContained `
            -o $publish --nologo
    }

    # The installer embeds whatever is in artifacts\publish, so it must be built
    # after the publish step rather than as part of the solution build above.
    New-Item -ItemType Directory -Force -Path $dist | Out-Null
    Invoke-Step 'Packaging the installer' {
        dotnet publish src\Logzilla.Setup\Logzilla.Setup.csproj `
            -c $Configuration -r $Runtime --self-contained true `
            -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true `
            -o $dist --nologo
    }

    Get-ChildItem $dist -Filter 'Logzilla-Setup*' |
        ForEach-Object { Write-Host ("    {0}  ({1:N1} MB)" -f $_.FullName, ($_.Length / 1MB)) -ForegroundColor Green }

    Write-Host '==> Done' -ForegroundColor Green
}
finally {
    Pop-Location
}
