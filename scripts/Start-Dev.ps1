<#
.SYNOPSIS
  Starts the local Aspire stack with or without demo data.
.DESCRIPTION
  (default)  seeds fictional departments, teams, providers, models, routes and a dev key.
  -Empty     starts without demo data. Only the Keycloak test users exist (sign in as a
             gateway-admin), so you create the organisation, providers and keys yourself.
  Seeding only runs against an EMPTY database. To switch an existing environment between demo and
  empty, stop the stack and delete the AppHost's Postgres Docker volume first (see README).
#>
[CmdletBinding()]
param([switch]$Empty)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$apphost = Join-Path $root 'src\Ume.LlmGateway.AppHost\Ume.LlmGateway.AppHost.csproj'

$env:Seed__Enabled = if ($Empty) { 'false' } else { 'true' }
Write-Output ('Starting with {0}.' -f $(if ($Empty) { 'an empty database (no demo data)' } else { 'demo data' }))
aspire start --apphost $apphost --non-interactive
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
aspire wait admin-ui --apphost $apphost
