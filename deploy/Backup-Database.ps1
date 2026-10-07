param(
    [Parameter(Mandatory = $true)][string]$ComposeProject,
    [Parameter(Mandatory = $true)][string]$Destination
)
$ErrorActionPreference = 'Stop'
if (Test-Path $Destination) { throw 'Destination already exists; choose a new protected backup path.' }
$id = docker ps -q --filter "label=com.docker.compose.project=$ComposeProject" --filter 'label=com.docker.compose.service=postgres'
if ($LASTEXITCODE -ne 0 -or @($id).Count -ne 1 -or -not $id) { throw 'Select exactly one running production Postgres container.' }
$file = '/var/lib/postgresql/data/ume-backup-' + [Guid]::NewGuid().ToString('N') + '.dump'
try {
    ('export PGPASSWORD="$(cat /run/postgres-secrets/bootstrap_password)"; exec pg_dump -U postgres -d gatewaydb -Fc -f ' + $file) |
        docker exec -i $id sh -c "tr -d '\r' | sh"
    if ($LASTEXITCODE -ne 0) { throw 'Database backup failed.' }
    docker cp "${id}:$file" $Destination
    if ($LASTEXITCODE -ne 0) { throw 'Copying backup failed.' }
    Write-Output 'Database archive copied. Apply approved encryption, access and retention policy.'
} finally {
    docker exec $id rm -f $file
}
