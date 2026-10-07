param([switch]$SkipBuild)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$project = 'ume-proof-' + [Guid]::NewGuid().ToString('N').Substring(0, 10)
$volume = $project + '-memory'
$provisionImage = 'mcr.microsoft.com/dotnet/sdk:10.0.401-noble@sha256:e70cdb7f80b0348f5cb85f19a8f670fca061f033d57eed12fa003d58b0e06317'
function Check-Exit { if ($LASTEXITCODE -ne 0) { throw "Command failed with exit code $LASTEXITCODE." } }
$compose = @('compose', '-p', $project, '-f', "$PSScriptRoot\generated\docker-compose.yaml", '-f', "$PSScriptRoot\compose.hardening.yaml")
$composeConfigured = $false
$volumeCreated = $false
$holder = $null
$archive = Join-Path ([IO.Path]::GetTempPath()) ($project + '.dump')
$imageTag = dotnet msbuild "$repo\src\Ume.LlmGateway.Gateway\Ume.LlmGateway.Gateway.csproj" -getProperty:ContainerImageTag -nologo
Check-Exit
try {
    if (-not $SkipBuild) {
        Push-Location "$repo\src\admin-ui"
        try { npm run build; Check-Exit } finally { Pop-Location }
        foreach ($service in @('Gateway', 'AdminApi', 'MigrationService')) {
            dotnet publish "$repo\src\Ume.LlmGateway.$service\Ume.LlmGateway.$service.csproj" -c Release /t:PublishContainer /p:PublishAdminUi=true -v q -nologo
            Check-Exit
        }
    }
    docker volume create --driver local --opt type=tmpfs --opt device=tmpfs --opt o=size=32m $volume | Out-Null
    Check-Exit
    $volumeCreated = $true
    $mount = docker volume inspect $volume --format '{{.Mountpoint}}'
    Check-Exit
    $holder = docker run -d --mount "type=volume,source=$volume,target=/workspace" --mount "type=bind,source=$PSScriptRoot\redis,target=/config,readonly" --entrypoint sleep $provisionImage infinity
    Check-Exit
    Get-Content "$PSScriptRoot\tests\provision.sh" -Raw | docker exec -i $holder sh -c "tr -d '\r' | sh"
    Check-Exit
    $env:UME_CERTS_DIR = "$mount/certs"
    $env:UME_SECRETS_DIR = "$mount/secrets"
    $env:UME_OIDC_AUTHORITY = 'https://idp.example.invalid/realms/ume'
    $env:UME_GATEWAY_PUBLIC_URL = 'https://localhost:18443'
    $env:UME_GATEWAY_PORT = '18443'
    $env:UME_ADMIN_PORT = '19443'
    $env:GATEWAY_PORT = '8443'
    $env:ADMINAPI_PORT = '8443'
    $env:GATEWAY_IMAGE = docker image inspect "ume-llm-gateway/gateway:$imageTag" --format '{{.Id}}'
    Check-Exit
    $env:ADMINAPI_IMAGE = docker image inspect "ume-llm-gateway/adminapi:$imageTag" --format '{{.Id}}'
    Check-Exit
    $env:MIGRATIONS_IMAGE = docker image inspect "ume-llm-gateway/migrations:$imageTag" --format '{{.Id}}'
    Check-Exit
    $composeConfigured = $true
    $model = docker @compose config --format json | ConvertFrom-Json
    Check-Exit
    if (@($model.services.psobject.Properties).Count -ne 5) { throw 'Unexpected production services.' }
    foreach ($name in @('postgres', 'redis')) {
        $service = $model.services.$name
        if ($service.ports -or $service.networks.psobject.Properties.Name -notcontains 'data') { throw 'Data service isolation failed.' }
    }
    if (-not $model.networks.data.internal) { throw 'Data network is not internal.' }
    foreach ($service in $model.services.psobject.Properties.Value) {
        if (-not $service.read_only -or $service.cap_drop -notcontains 'ALL') { throw 'Container hardening missing.' }
        if ($service.image -notmatch 'sha256:') { throw 'Images must use immutable digests.' }
    }
    docker @compose up -d --wait --wait-timeout 180
    if ($LASTEXITCODE -ne 0) {
        foreach ($name in @('postgres', 'redis', 'migrations', 'gateway', 'adminapi')) {
            $id = docker ps -aq --filter "label=com.docker.compose.project=$project" --filter "label=com.docker.compose.service=$name"
            if ($id) {
                $ErrorActionPreference = 'Continue'
                $lines = docker logs $id 2>&1
                $ErrorActionPreference = 'Stop'
                $lines | Where-Object { "$_" -match 'error|fatal|can.t|failed|denied|not permitted|not found' -and "$_" -notmatch 'STATEMENT:|DETAIL:|CONTEXT:' } |
                    ForEach-Object { Write-Output ("$name`: $_" -replace '[A-Fa-f0-9]{64,}', '[redacted]') }
            }
        }
        throw 'Production Compose startup failed.'
    }
    Check-Exit
    foreach ($name in @('postgres', 'redis')) {
        $id = docker @compose ps -q $name
        Check-Exit
        Get-Content "$PSScriptRoot\tests\verify-data.sh" -Raw | docker exec -i $id sh -c "tr -d '\r' | sh -s -- $name"
        Check-Exit
    }
    foreach ($name in @('gateway', 'adminapi')) {
        $id = docker @compose ps -q $name
        $state = docker inspect $id --format '{{.State.Health.Status}}'
        Check-Exit
        if ($state -ne 'healthy') { throw "$name is not healthy." }
    }
    $net = $project + '_edge'
    $probe = 'curl -fsS --cacert /workspace/ca.pem https://adminapi:8443/ | grep -q ''id="app"''; curl -fsS --cacert /workspace/ca.pem https://adminapi:8443/settings | grep -q ''type="module"''; curl -fsS --cacert /workspace/ca.pem https://gateway:8443/health/ready | grep -q Healthy'
    $probe | docker run --rm -i --network $net --mount "type=volume,source=$volume,target=/workspace,readonly" --entrypoint 'bash' $provisionImage -c "tr -d '\r' | bash -e"
    Check-Exit
    Push-Location $repo
    try { node "$PSScriptRoot\tests\spa-smoke.mjs" $holder; Check-Exit } finally { Pop-Location }
    & "$PSScriptRoot\Backup-Database.ps1" -ComposeProject $project -Destination $archive
    docker @compose stop gateway adminapi | Out-Null
    Check-Exit
    & "$PSScriptRoot\Restore-Database.ps1" -ComposeProject $project -Backup $archive -ConfirmRestore
    docker @compose up -d --wait --wait-timeout 180 | Out-Null
    Check-Exit
    Write-Output 'Hardened production Compose: five immutable, nonroot, read-only services; TLS/ACL/roles, readiness and SPA verified.'
} finally {
    if ($composeConfigured) { docker @compose down --volumes --remove-orphans | Out-Null }
    if ($holder) { docker rm -f $holder | Out-Null }
    if ($volumeCreated) { docker volume rm $volume | Out-Null }
    if (Test-Path $archive -PathType Leaf) { Remove-Item -LiteralPath $archive }
}
