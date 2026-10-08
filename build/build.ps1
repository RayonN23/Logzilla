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

.PARAMETER E2E
    Also run the end-to-end tests, which start the real app and drive it. They
    open windows and send keystrokes, so they are off by default.

.PARAMETER FrameworkDependent
    Publish against an installed .NET runtime instead of bundling one.

.PARAMETER BundleCopilot
    Carry the GitHub Copilot runtime inside the app (about 200 MB), so Copilot
    works without the user installing the Copilot CLI. Off by default: the
    installer stays small, and organisations that turn AI off never ship it.

.PARAMETER GitHubClientId
    The Client ID of the GitHub OAuth App that "Sign in with GitHub" uses for
    Copilot. Overrides the one in Logzilla.App.csproj. It is not a secret.

.EXAMPLE
    .\build\build.ps1
    .\build\build.ps1 -Runtime win-arm64
    .\build\build.ps1 -BundleCopilot
    .\build\build.ps1 -E2E
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [string]$Runtime = 'win-x64',
    [switch]$SkipTests,
    [switch]$E2E,
    [switch]$FrameworkDependent,
    [switch]$BundleCopilot,
    [string]$GitHubClientId = ''
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
        # The unit tests only. The end-to-end tests open windows and type, so they run
        # only when asked for with -E2E.
        $unitTests = @(
            'tests\Logzilla.Core.Implementation.Tests\Logzilla.Core.Implementation.Tests.csproj',
            'tests\Logzilla.AI.Tests\Logzilla.AI.Tests.csproj',
            'tests\Logzilla.App.Tests\Logzilla.App.Tests.csproj'
        )
        foreach ($project in $unitTests) {
            Invoke-Step "Testing $(Split-Path -Leaf (Split-Path -Parent $project))" {
                dotnet test $project -c $Configuration --nologo --no-build
            }
        }
    }

    if ($E2E) {
        Invoke-Step 'End-to-end tests (Logzilla windows will open; leave the keyboard alone)' {
            $env:LOGZILLA_E2E = '1'
            try {
                dotnet test tests\Logzilla.App.E2E\Logzilla.App.E2E.csproj -c $Configuration --nologo --no-build
            }
            finally {
                Remove-Item Env:\LOGZILLA_E2E -ErrorAction SilentlyContinue
            }
        }
    }

    if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }

    $selfContained = (-not $FrameworkDependent).ToString().ToLowerInvariant()
    $bundle = $BundleCopilot.IsPresent.ToString().ToLowerInvariant()
    # Passed only when given: an empty command-line property would override the
    # default Client ID in the project file with nothing.
    $extra = @()
    if ($GitHubClientId) { $extra += "-p:GitHubClientId=$GitHubClientId" }
    Invoke-Step "Publishing the app ($Runtime, self-contained=$selfContained, Copilot bundled=$bundle)" {
        dotnet publish src\Logzilla.App\Logzilla.App.csproj `
            -c $Configuration -r $Runtime --self-contained $selfContained -p:BundleCopilot=$bundle `
            -o $publish --nologo @extra
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
