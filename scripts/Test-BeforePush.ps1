<#
.SYNOPSIS
  Runs the checks of the Verify POC workflow locally, so a pull request is green on its first CI run.
.DESCRIPTION
  Actions minutes are limited (docs/local-development.md, "CI and Actions minutes"). This runs what CI runs:
  a Release build of the solution (warnings and unnecessary usings are errors), the tests without the Flaky ones, the
  .NET and npm vulnerability audits, and the admin UI's lint, typecheck, tests and build.
  -Quick   skips the backend tests (they need Docker and take a few minutes).
  -NoUi    skips the admin UI and e2e checks.
  Warns when the local .NET SDK is not the one CI uses: a different SDK reports different unnecessary usings.
  Works in Windows PowerShell 5.1 and in pwsh on Linux and macOS.
#>
[CmdletBinding()]
param([switch]$Quick, [switch]$NoUi)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

function Invoke-Step([string]$Name, [scriptblock]$Command) {
    Write-Output "== $Name"
    & $Command
    if ($LASTEXITCODE -ne 0) { throw "$Name failed (exit $LASTEXITCODE)." }
}

$ciSdk = (Select-String -Path '.github/workflows/verify.yml' -Pattern 'dotnet-version:\s*(\S+)').Matches[0].Groups[1].Value
$localSdk = (dotnet --version).Trim()
if ($localSdk -ne $ciSdk) {
    Write-Warning "Local .NET SDK $localSdk, CI uses $ciSdk. Install $ciSdk, or CI may flag usings this build accepts."
}

Invoke-Step 'Build (Release, like CI)' { dotnet build UmeLlmGateway.slnx -c Release --no-incremental --nologo -v:q }
if (-not $Quick) {
    Invoke-Step 'Backend tests (without Flaky)' {
        dotnet test --solution UmeLlmGateway.slnx -c Release --no-build --filter-not-trait 'Category=Flaky'
    }
}
Invoke-Step '.NET vulnerability audit' {
    $audit = dotnet list UmeLlmGateway.slnx package --vulnerable --include-transitive --format json | ConvertFrom-Json
    $vulnerable = @($audit.projects | ForEach-Object { $_.frameworks } | ForEach-Object { @($_.topLevelPackages) + @($_.transitivePackages) } |
        Where-Object { $_ -and $_.vulnerabilities })
    if ($vulnerable.Count -gt 0) { $vulnerable | ForEach-Object { Write-Output "  $($_.id) $($_.resolvedVersion)" }; $global:LASTEXITCODE = 1 }
}

if (-not $NoUi) {
    Push-Location 'src/admin-ui'
    try {
        Invoke-Step 'Admin UI install' { npm ci --no-audit --no-fund }
        Invoke-Step 'Admin UI lint' { npm run lint }
        Invoke-Step 'Admin UI typecheck' { npm run typecheck }
        Invoke-Step 'Admin UI tests' { npm test }
        Invoke-Step 'Admin UI build' { npm run build }
        Invoke-Step 'Admin UI audit' { npm audit --audit-level=low }
    } finally { Pop-Location }
    Push-Location 'tests/e2e'
    try {
        Invoke-Step 'Browser tests install' { npm ci --no-audit --no-fund }
        Invoke-Step 'Browser tests typecheck' { npx tsc --noEmit }
        Invoke-Step 'Browser tests audit' { npm audit --audit-level=low }
    } finally { Pop-Location }
}

Write-Output 'All checks passed. Push once you are done with the change: every push to a ready pull request runs CI.'
