# Keycloak (optional)

The gateway signs admins in through any OpenID Connect provider ([identity and roles](identity-and-roles.md)). If an
organisation has none it wants to use directly, `deploy/keycloak` runs a production Keycloak next to the gateway: a
Compose project of its own, operated with the gateway, with its database in the gateway's Postgres. Other systems can
share its realm `ume` with their own clients and roles (each system's part is a file), or run without it.

Nothing in the gateway depends on this project. Leave it out and point `UME_OIDC_AUTHORITY` at your own provider.

| Path | What it is |
|---|---|
| `deploy/keycloak/compose.yaml` | Keycloak (production mode) and `realm-import`, the job that applies the realm files |
| `deploy/keycloak/Dockerfile` | The pinned Keycloak image, built with `kc.sh build` so it starts optimized with a read-only file system |
| `deploy/keycloak/init-keycloak.sh` | Certificate from the gateway's internal CA, secrets, `deploy/keycloak/.env` |
| `deploy/keycloak/realm/10-gateway.json` | The gateway's part of realm `ume` and the realm-wide settings (below) |
| `deploy/keycloak/additions/` | Other systems' realm files, applied after the gateway's (empty in this repository) |
| `deploy/postgres/add-keycloak-database.sql` | Adds role `ume_keycloak` and database `keycloak` to an existing installation |
| `deploy/tests/keycloak-e2e.sh` | End-to-end test with Docker only (below) |

## Set up

Run the gateway's `deploy/init-deployment.sh` first; this setup uses its internal CA and secret directories.

1. **Certificates, secrets and settings:**
   ```bash
   ./deploy/keycloak/init-keycloak.sh --host auth.example.se
   ```
   It issues Keycloak's certificate (names `keycloak` and the host) from the gateway's CA, creates the database password,
   a temporary admin password and the import job's client secret, and uses the admin API's OIDC client secret
   (`secrets/adminapi/Oidc__ClientSecret`, created if missing) for the `ume-admin` client. Re-running keeps every
   existing secret and setting. Options: `--port` (default 11443), `--bind`, `--admin-url`, `--renew-certs`.
2. **Database.** A new installation gets it when Postgres is first created, as long as step 1 ran before (`init.sql`
   reads `secrets/postgres/keycloak_password`). An existing one adds it once (idempotent):
   ```bash
   docker compose -f deploy/compose.prod.yaml --env-file deploy/.env up -d postgres   # picks up the new pg_hba.conf
   docker compose -f deploy/compose.prod.yaml --env-file deploy/.env exec -T postgres \
     sh -c 'PGPASSWORD=$(cat /run/postgres-secrets/bootstrap_password) psql -q -v ON_ERROR_STOP=1 -U postgres -d gatewaydb' \
     < deploy/postgres/add-keycloak-database.sql
   ```
3. **Start:**
   ```bash
   docker compose -f deploy/keycloak/compose.yaml --env-file deploy/keycloak/.env up -d --build --wait
   ```
   `realm-import` runs after Keycloak is ready and exits; `docker compose ... logs realm-import` shows what it did.
4. **Reverse proxy.** Forward `https://auth.example.se` to the HTTPS port (re-encrypting, like the gateway's hosts in
   `deploy/proxy/nginx.conf.example`) with `X-Forwarded-For`, `X-Forwarded-Proto` and `X-Forwarded-Host`; Keycloak
   trusts them (`proxy-headers=xforwarded`), so the port must only be reachable from the proxy. A proxy health check
   can use `/realms/ume/.well-known/openid-configuration`; Keycloak's own `/health/ready` is on the management port,
   which is not published.
5. **Point the gateway at it:** `UME_OIDC_AUTHORITY=https://auth.example.se/realms/ume` in `deploy/.env`, then
   `docker compose ... up -d adminapi`.
6. **Admins of Keycloak itself.** Sign in to `https://auth.example.se/admin` as `bootstrap-admin` (password in
   `secrets/keycloak/admin_password`), create personal admin users in the *master* realm, then delete
   `bootstrap-admin`. Keep the client `ume-realm-import` in *master*: the import job signs in with it.

### Production settings

`compose.yaml` follows Keycloak's production guide: `start --optimized` on the image built from the `Dockerfile`
(PostgreSQL, local caches for one instance, health and metrics on the management port), a fixed hostname
(`KC_HOSTNAME`), TLS on 8443 only, the database over TLS with the CA checked, `http-max-queued-requests` (default
1000, `UME_KEYCLOAK_MAX_QUEUED_REQUESTS`) so overload answers 503 instead of queueing without bound, and the same
container hardening as the gateway (read-only file system, no capabilities). Memory: Keycloak's sizing guide asks
about 1.25 GB for one instance; the limit is 1.5 GB (`UME_KEYCLOAK_MEMORY`). Secrets are files; the three Keycloak can
only read from its environment are set for the process at start and do not show in `docker inspect`.

**Updates.** The image is pinned by digest in the `Dockerfile`, like every image in this repository: update only to
a build at least 7 days old, then `up -d --build --wait`. Keycloak migrates its database on start, so back it up first.

## Realm files

`realm-import` is [keycloak-config-cli](https://github.com/adorsys/keycloak-config-cli). On every `up` it applies
`realm/*.json`, then `additions/*.json` (or `UME_KEYCLOAK_ADDITIONS_DIR`), in name order. A file is a Keycloak realm
representation with `"realm": "ume"` and only the parts it owns: roles, clients, scope mappings, identity providers,
mappers, groups.

- **Placeholders:** `$(env:NAME)` and `$(env:NAME:-default)` from the job's environment (variables for other systems'
  files go in a file named by `UME_KEYCLOAK_IMPORT_ENV`), and `$(file:UTF-8:/path)` for a secret file mounted into the
  job. Keycloak's own `${...}` texts (such as `${username}`) are left alone. A placeholder without a value fails the import.
- **Adds and updates, never deletes.** Every resource type is imported with `no-delete`: a file cannot remove what
  another file or an administrator made. When a file drops a client or role, remove it in the admin console.
- **Realm-wide settings belong to the gateway's file:** password policy (12–128 characters, not the user name or
  e-mail, the last five not reused), brute-force protection (10 failures, increasing waits up to 15 minutes), no self
  registration, and the user profile with `departmentCodes` and `teamCodes`. Another system's file must not declare
  `userProfile` (it replaces the whole profile) or realm settings. A system that runs its own Keycloak declares the
  same settings in its own file.
- **Each client sees only its system's roles.** Set `"fullScopeAllowed": false` on every client and list the roles
  its tokens may carry in `scopeMappings`, as `10-gateway.json` does for `ume-admin`. A user with roles in two systems
  then gets each system's roles only in that system's tokens (tested).
- **Keeping another system's part in step.** That system delivers its file; the gateway operator puts a released
  version of it in the additions directory, sets its placeholders, and runs `up -d --wait` (only the import job runs
  again). Try a new file on a test installation first (the end-to-end test shows how). An import that fails stops at
  that file: the files before it stay applied and sign-in keeps working, but the failing file may be partly applied,
  so fix it and run the job again.

## Two-factor sign-in (OTP)

Not enforced: whether people use a one-time code is the deploying organisation's decision. Options:
- **Everyone:** *Authentication → Required actions → Configure OTP*, turn on *Set as default action* (new users), or add
  the required action to existing users.
- **Some roles only:** copy the *browser* flow, add a conditional sub-flow with *Condition - user role* and *OTP Form*,
  and bind the copy as the browser flow. Put it in a realm file so it survives rebuilds.
- **From the organisation's directory:** when users come through a brokered Entra ID or AD FS, their MFA applies there.

## Identity providers: brokered or direct

An organisation with Entra ID or Active Directory can use it in two ways; both keep the token shape the apps expect
(`roles`, `departmentCodes`, `teamCodes`), so no app changes:

- **Direct:** the apps use Entra ID or AD FS themselves, without Keycloak (`Oidc:*` settings,
  [identity and roles](identity-and-roles.md)).
- **Brokered:** Keycloak signs users in through the organisation's provider and maps its claims to the realm's roles
  and attributes. Several systems then share one setup, and local accounts (for example a supplier) can coexist.

A brokered provider can be a realm file in the additions directory. The end-to-end test signs a user in through a
second Keycloak realm standing in for the organisation's provider (`deploy/tests/keycloak-e2e.sh`): a group in the
organisation's token gives the role `gateway-admin` and its `department` claim becomes `departmentCodes`.

**Entra ID (documented, not yet verified against a tenant):**
1. App registration (single tenant), *Web* redirect URI `https://auth.example.se/realms/ume/broker/entra/endpoint`,
   a client secret; *Token configuration*: optional claim `email`, and a groups claim (*Security groups*, emitted as
   object ids) or app roles.
2. Identity provider `entra`, type *OpenID Connect v1.0*, discovery
   `https://login.microsoftonline.com/<tenant id>/v2.0/.well-known/openid-configuration`. The tenant-specific URL only
   accepts tokens from that tenant (`tid`); guests of the tenant can still sign in unless the app requires assignment.
3. Mappers: *Advanced Claim to Role* (`groups` contains the object id of the admin group → `gateway-admin`, and so on)
   and *Attribute Importer* (the claim that holds the cost-centre code → `departmentCodes`).

**Active Directory through LDAP (documented, not tested here):** *User federation → LDAP*, vendor *Active Directory*,
`ldaps://`, a read-only bind account, edit mode *READ_ONLY*; a *role-ldap-mapper* (or *group-ldap-mapper* plus a
group-to-role mapping) for the roles and a *user-attribute-ldap-mapper* from the attribute that holds the cost-centre
code (for example `departmentNumber`) to `departmentCodes`. **AD FS** can be brokered like Entra ID (OpenID Connect,
discovery at `https://adfs.example.se/adfs/.well-known/openid-configuration`).

## Accounts

The realm is administered by whoever runs the gateway unless the organisation decides otherwise. In the admin console
(`https://auth.example.se/admin`, realm `ume`, *Users*):

| Task | How |
|---|---|
| Create an account | *Add user*: user name, e-mail, first and last name; *Department codes* and *Team codes* (the cost-centre and team codes as in the gateway's *Organisation*); then *Role mapping → Assign role* (`gateway-admin`, `department-admin`, `team-admin`, `viewer`, or another system's roles) |
| First password | *Credentials → Set password* with *Temporary* on; hand it over out of band. Keycloak asks for a new one at the first sign-in (no e-mail is configured) |
| Disable | Turn off *Enabled*: the user cannot sign in again. A gateway admin UI session already open lasts until it ends, at most 12 hours ([sessions](identity-and-roles.md#sessions)); other systems have their own rules. Delete the account only when its audit value is gone |
| Forgotten password | *Credentials → Reset password*, temporary |
| Locked out | After repeated failures brute-force protection locks the account for a while; *Enabled* shows it, and *Unlock* (or waiting) ends it |
| Lost OTP device (if OTP is used) | *Credentials*: delete the OTP credential; the user sets up a new one at the next sign-in |
| Change departments or teams | Edit the attributes; the gateway reads them at the next sign-in |

Brokered and LDAP users are created by their first sign-in and kept in step by the mappers (*sync mode Force*); manage
them in the organisation's directory.

## Backup and restore

The `keycloak` database is backed up separately from `gatewaydb`, so either can be restored without rolling back the
other (`docs/runbook.md`, *Backup and restore*):

```powershell
deploy\Backup-Database.ps1 -ComposeProject ume-gateway -Database keycloak -Destination D:\backups\keycloak-2026-11-06.dump
# restore: stop Keycloak first (docker compose -f deploy/keycloak/compose.yaml stop keycloak)
deploy\Restore-Database.ps1 -ComposeProject ume-gateway -Database keycloak -Backup D:\backups\keycloak-2026-11-06.dump -ConfirmRestore
```

The dump holds the realm, accounts, password hashes and OTP secrets: protect it like the gateway's. The secrets in
`secrets/keycloak` are not in it; keep them with the gateway's secrets. Realm files are applied again on the next `up`.

## Test

```bash
deploy/tests/keycloak-e2e.sh        # about 3 minutes, Docker only; KEEP=1 leaves it running
```

It runs `init-keycloak.sh` against a stand-in gateway deployment, starts the gateway's Postgres image with
`init.sql` and `pg_hba.conf`, starts Keycloak in production mode, imports the gateway's file plus another system's file
and a brokered provider, and checks the realm settings, that each client's token carries only its own system's roles,
a brokered sign-in (roles and `departmentCodes` from the organisation's claims), that a second import deletes
nothing, that `add-keycloak-database.sql` is idempotent, a backup and restore of the `keycloak` database (with
`Backup-Database.ps1`/`Restore-Database.ps1` when `pwsh` is installed) and the container hardening.
