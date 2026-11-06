param(
    [Parameter(Mandatory = $true)][string]$ComposeProject,
    [Parameter(Mandatory = $true)][string]$Backup,
    [switch]$ConfirmRestore,
    # gatewaydb (default), or keycloak for the optional Keycloak (deploy/keycloak); see Backup-Database.ps1.
    [ValidateSet('gatewaydb', 'keycloak')][string]$Database = 'gatewaydb',
    # The Compose project of the optional Keycloak, checked to be stopped before its database is restored.
    [string]$KeycloakProject = 'ume-keycloak'
)
$ErrorActionPreference = 'Stop'
if (-not $ConfirmRestore) { throw "Restore replaces $Database objects. Stop the services that use it and explicitly pass -ConfirmRestore." }
if (-not (Test-Path $Backup -PathType Leaf)) { throw 'Backup file not found.' }
if ($Database -eq 'gatewaydb') {
    $active = docker ps -q --filter "label=com.docker.compose.project=$ComposeProject" --filter 'label=com.docker.compose.service=gateway'
    $admin = docker ps -q --filter "label=com.docker.compose.project=$ComposeProject" --filter 'label=com.docker.compose.service=adminapi'
    $migrator = docker ps -q --filter "label=com.docker.compose.project=$ComposeProject" --filter 'label=com.docker.compose.service=migrations'
    if ($active -or $admin -or $migrator) { throw 'Stop gateway, adminapi and migrations before restore.' }
} else {
    $keycloak = docker ps -q --filter "label=com.docker.compose.project=$KeycloakProject" --filter 'label=com.docker.compose.service=keycloak'
    if ($keycloak) { throw "Stop Keycloak ($KeycloakProject) before restoring its database." }
}
$id = docker ps -q --filter "label=com.docker.compose.project=$ComposeProject" --filter 'label=com.docker.compose.service=postgres'
if ($LASTEXITCODE -ne 0 -or @($id).Count -ne 1 -or -not $id) { throw 'Select exactly one running production Postgres container.' }
$file = '/var/lib/postgresql/data/ume-restore-' + [Guid]::NewGuid().ToString('N') + '.dump'
try {
    docker cp $Backup "${id}:$file"
    if ($LASTEXITCODE -ne 0) { throw 'Copying backup failed.' }
    ('export PGPASSWORD="$(cat /run/postgres-secrets/bootstrap_password)"; exec pg_restore -U postgres -d ' + $Database + ' --clean --if-exists --exit-on-error ' + $file) |
        docker exec -i $id sh -c "tr -d '\r' | sh"
    if ($LASTEXITCODE -ne 0) { throw 'Database restore failed; do not start the services that use it.' }
    if ($Database -eq 'gatewaydb') {
        Write-Output 'Database restored. Run migration/role grants and verify recovery before starting APIs.'
    } else {
        Write-Output 'Keycloak database restored. Start Keycloak (deploy/keycloak) and check that you can sign in.'
    }
} finally {
    docker exec $id rm -f $file
}
