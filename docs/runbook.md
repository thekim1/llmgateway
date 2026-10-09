# Operational runbook

## Ownership and production prerequisites

Assign a service owner, on-call function, DPO/security contacts and records owner.
Approve RPO/RTO, retention, IdP, providers, egress policy and restore procedures.
Use a managed Linux Docker host (Docker 28+, Compose supporting `!override`/`!reset`),
municipal PKI and external HTTPS OIDC. The development Keycloak realm is not production IAM.
`Ollama:Enabled` is local run-mode only; publishing with it enabled fails explicitly.
For production Ollama/vLLM, provide an independently managed approved HTTPS endpoint
with municipal network/egress controls rather than deploying an unhardened HTTP container.
Read [security/compliance](security-and-compliance.md) before introducing real data.

## Quick start (recommended)

After building and pushing the images (next section), run `deploy/init-deployment.sh` on the
Docker host, then
`docker compose -f deploy/compose.prod.yaml --env-file deploy/.env up -d --wait`.
The script generates everything in *Nonsecret deployment inputs* and *Certificate and secret
layout* below (internal CA, certificates, secrets, `deploy/.env`) and prints the OIDC redirect
URIs to register. It never overwrites existing secrets; `--renew-certs` re-issues service
certificates (generated leaves last 825 days, the CA and Data Protection certificate 10 years).
For municipal PKI, replace the generated certificate files (same names) before starting.
The sections below describe the same layout for operators who provision it by other means,
and the Aspire-generated base plus `compose.hardening.yaml`.

## Identity provider (existing Keycloak, AD FS, Entra ID, Active Directory)

The bundled Keycloak realm (`dev/keycloak`) is for local runs only; **production always uses an
existing OIDC provider** (`UME_OIDC_AUTHORITY` in `deploy/.env`). The admin API needs a client
(authorization code + PKCE; redirect URIs `https://<admin host>/signin-oidc` and
`/signout-callback-oidc`) and these
claims **in the ID token**:

| Claim | Meaning |
|---|---|
| `roles` | `gateway-admin`, `department-admin` and/or `viewer` |
| `departmentCodes` | Department codes a `department-admin`/`viewer` may see (strings or a JSON array) |
| `name`, `email` | Display |

If the provider cannot emit those names or values, remap them instead of changing the provider
(settings in `deploy/.env`; for local runs the same keys as `Oidc:<Setting>` in AppHost user secrets):

| Setting | Env variable | Purpose |
|---|---|---|
| `Oidc:RoleClaim` / `DepartmentClaim` | `UME_OIDC_ROLE_CLAIM` / `UME_OIDC_DEPARTMENT_CLAIM` | Read roles/department codes from a differently named claim (`role`, `department`) |
| `Oidc:GroupClaim` | `UME_OIDC_GROUP_CLAIM` | Claim holding group names (default `groups`) |
| `Oidc:RoleGroups:<role>` | `UME_OIDC_GROUPS_GATEWAY_ADMIN`, `_DEPARTMENT_ADMIN`, `_VIEWER` | Groups (`;`-separated, case-insensitive) that grant a role, so no IdP-side roles are needed |
| `Oidc:ExtraScopes:0` | `UME_OIDC_EXTRA_SCOPE` | Additional scope to request (`groups`) |
| `Oidc:ClientSecret` | `--oidc-client-secret-file` (init script) | Confidential client; stored as the `Oidc__ClientSecret` secret file |

Users without a granted role can sign in but are refused (403) by the role policies; there is no default role.

**Active Directory.** Do not talk LDAP directly; use one of:

1. *An existing Keycloak federated to AD (recommended if one exists).* Create a client `ume-admin` in
   the realm. Add the AD groups to the realm via the LDAP provider's *group-ldap-mapper* (or
   *role-ldap-mapper* to get realm roles named `gateway-admin` etc. directly) and a token mapper:
   either *User Realm Role* with claim `roles`, or *Group Membership* (full path off) with claim
   `groups` plus `UME_OIDC_GROUPS_*`. Map an AD attribute (for example `department`) to a user attribute with the
   LDAP *user-attribute-mapper* and expose it with a *User Attribute* mapper, claim `departmentCodes`, multivalued.
   Add all mappers to the ID token. Authority: `https://<keycloak>/realms/<realm>`.
2. *AD FS* (OpenID Connect application group): issue the AD group membership (`group` claim, names
   as the token shows them, e.g. `DOMAIN\\GG-Llm-Admins`) and a department attribute as claims in the ID token. Set
   `UME_OIDC_GROUP_CLAIM=group`, `UME_OIDC_DEPARTMENT_CLAIM=<claim>` and `UME_OIDC_GROUPS_*`. Authority:
   `https://<adfs>/adfs`.
3. *Entra ID (hybrid AD)*: app roles named `gateway-admin` etc. arrive in `roles` with no mapping; the
   department needs an optional or custom claim. Authority: `https://login.microsoftonline.com/<tenant>/v2.0`.

Only the Keycloak realm defaults are tested end to end; the AD FS and Entra settings are the intended
mapping and should be verified against the real IdP: sign in, then check `/bff/user`, which shows the
`roles` and `departmentCodes` the API received. Test all three roles and department scoping.

**Local development against an existing provider.** Set `Oidc:Authority` (and `Oidc:ClientId`,
`Oidc:ClientSecret`, any mapping settings above) in AppHost user secrets. The AppHost then does not start
the bundled Keycloak and passes all `Oidc:*` settings to the admin API. Register the local admin
callback URL (see the dashboard) at the provider. The test users from `dev/keycloak` no longer apply.

## Reverse proxy and PKI

`deploy/init-deployment.sh` supports three ways of exposing the stack. Pick the one that
matches your environment; they combine (a proxy in front, with PKI certificates behind it).

| Setup | Command | Public URLs |
|---|---|---|
| Direct, internal CA (default; tests, small sites) | `init-deployment.sh` | `https://host:8443` (gateway), `https://host:9443` (admin UI) |
| Behind a reverse proxy (Nginx Proxy Manager, nginx, F5, ingress) | `init-deployment.sh --proxy` | `https://llm.example.se`, `https://llm-admin.example.se` (443) |
| Certificates from your PKI | `init-deployment.sh --gateway-cert ... --gateway-key ... --admin-cert ... --admin-key ... [--ca-file chain.pem]` | as above |

**Proxy mode.** Both services only speak HTTPS, so the proxy terminates the public TLS
(with its own certificate for the public name) and **re-encrypts** to
`https://<docker-host>:8443` (gateway) and `https://<docker-host>:9443` (admin UI). Requirements:

- Two distinct hostnames, one per service: the proxy routes by name.
- Preserve the `Host` header (`proxy_set_header Host $host;`, the default in Nginx Proxy
  Manager). The admin UI builds its OIDC redirect URIs from it, so register
  `https://<admin host>/signin-oidc` and `/signout-callback-oidc` in the IdP.
- Do not enable forwarded-header trust; it is not needed because the upstream hop is HTTPS.
- Gateway host: disable response buffering and allow long reads (streamed completions can run
  for minutes), and allow bodies of a few MB or more.
- The upstream certificate is signed by the internal CA, so the proxy must either skip
  upstream verification (the nginx default; Nginx Proxy Manager does this) or trust
  `deploy/certs/ca.pem` and use `proxy_ssl_name gateway;` / `adminapi;`. With F5, import
  the CA into the server-side SSL profile.
- Ports 8443/9443 are bound to `0.0.0.0` in this mode; restrict them to the proxy with the host
  firewall (or pass `--bind <address>` / put the proxy on the same host with `--bind 127.0.0.1`).

`deploy/proxy/nginx.conf.example` holds the complete nginx configuration (the script writes a filled-in
`proxy/nginx.conf`). In **Nginx Proxy Manager**, create two Proxy Hosts:

| Setting | Gateway host | Admin UI host |
|---|---|---|
| Scheme / Forward host / port | `https` / Docker host / 8443 | `https` / Docker host / 9443 |
| SSL tab | your certificate, Force SSL, HTTP/2 | same |
| Websockets Support | off | off |
| Advanced (custom nginx configuration) | `proxy_buffering off; proxy_read_timeout 600s; proxy_send_timeout 600s; client_max_body_size 32m;` | (none) |

**PKI certificates.** Pass the certificate chain and key for each public name (the
leaf certificate file should include intermediates). `--ca-file` appends your root and
intermediates to the services' trust store, which also needs to cover the IdP and
approved providers if those use a private CA (`SSL_CERT_FILE` replaces the system store).
Because a PKI certificate seldom covers the internal name `gateway`, the admin API then reaches
the gateway's operations endpoint through its public URL (`UME_GATEWAY_OPERATIONS_URL`
in `deploy/.env`): the container must be able to resolve and reach that name. Postgres and Redis
always keep internal certificates. PKI-supplied certificates are not touched by `--renew-certs`;
replace the files (or re-run with the options) before they expire and restart the services.

## Build and publish

Stop the exact local AppHost before backend builds. Generate from the AppHost, never
hand-edit the generated YAML:

```powershell
aspire stop --apphost src\Ume.LlmGateway.AppHost\Ume.LlmGateway.AppHost.csproj --non-interactive
aspire publish -o deploy\generated --non-interactive
Set-Location src\admin-ui
npm ci
npm run build
Set-Location ..\..
dotnet publish src\Ume.LlmGateway.Gateway -c Release /t:PublishContainer
dotnet publish src\Ume.LlmGateway.AdminApi -c Release /t:PublishContainer /p:PublishAdminUi=true
dotnet publish src\Ume.LlmGateway.MigrationService -c Release /t:PublishContainer
```

Release-tagged images use 0.1.0 currently. Push to the approved private registry and deploy
by immutable digest, not `latest`. .NET base and Postgres/Redis image digests are pinned.
Scan dependency/image reports and generate/review an SBOM per release (see upgrade guide).
No production `aspire prepare/deploy` secret-resolution file is required: this procedure
uses publish placeholders and operator-mounted secret files, not resolved `.env` secrets.

Always compose both files:
`docker compose -p ume-gateway -f deploy\generated\docker-compose.yaml -f deploy\compose.hardening.yaml config --quiet`.
Then run the same command with `up -d --wait --wait-timeout 180` instead of `config --quiet`.
Use host-native paths on Linux; the PowerShell examples are for the Windows build host.

## Nonsecret deployment inputs

Set `GATEWAY_IMAGE`, `ADMINAPI_IMAGE`, `MIGRATIONS_IMAGE` to approved `repository@sha256:...`.
Set `UME_CERTS_DIR`, `UME_SECRETS_DIR` to absolute operator-managed directories.
Set `UME_OIDC_AUTHORITY` (HTTPS), `UME_OIDC_CLIENT_ID`, `UME_GATEWAY_PUBLIC_URL` (HTTPS).
Optional `UME_GATEWAY_BIND/PORT` and `UME_ADMIN_BIND/PORT` default to loopback:8443/9443.
Base interpolation also uses `GATEWAY_PORT`/`ADMINAPI_PORT` (set 8443). Its discarded
`POSTGRES_PASSWORD`, `REDIS_PASSWORD`, `KEY_PEPPER` placeholders may be empty: the override
**replaces** these environments and file secrets are authoritative. Never put actual
secrets in those variables, generated `.env` or Compose command arguments.

Loopback publication is intentionally conservative. Use separate approved listeners,
TLS pass-through or an explicitly configured trusted reverse proxy. The override does
not trust arbitrary forwarded headers; do not enable generic forwarded-header trust.
If TLS is terminated by a proxy, separately configure trusted proxy IPs/headers and
verify cookie/redirect origins; direct HTTPS deployment avoids this requirement.

## Certificate and secret layout

Directories are mounted read-only. PostgreSQL UID/GID 70, Redis 999, .NET apps 1654.
Ensure parent traversal and service-only read permissions; private files 0600 and
directories 0700 on Linux. Windows ACLs must restrict the source before transfer.
Do not mount another service's secrets into an app.

| Directory | Required files |
|---|---|
| certs/postgres | ca.pem, postgres.crt, postgres.key (SAN postgres) |
| certs/redis | ca.pem, redis.crt, redis.key (SAN redis) |
| certs/gateway | ca.pem, gateway.crt/key (SAN gateway, localhost and public name), data-protection.pfx |
| certs/adminapi | ca.pem, adminapi.crt/key (SAN adminapi, localhost and public name), data-protection.pfx |
| certs/migrations | ca.pem, data-protection.pfx |
| secrets/postgres | bootstrap_password, migrator_password, gateway_password, admin_password |
| secrets/redis | users.acl based on users.acl.example, containing password SHA256 hash, not plaintext |
| secrets/migrations | ConnectionStrings__gatewaydb (ume_migrator), Security__KeyPepper, optional DataProtection__CertificatePassword |
| secrets/gateway | ConnectionStrings__gatewaydb (ume_gateway), ConnectionStrings__redis, Security__KeyPepper, optional DataProtection__CertificatePassword |
| secrets/adminapi | ConnectionStrings__gatewaydb (ume_admin), ConnectionStrings__redis, Security__KeyPepper, Oidc__ClientSecret if confidential client, optional DataProtection__CertificatePassword |

Use a secret manager to deliver files on a protected memory-backed mount. Application
files use `__` for config nesting. The HMAC pepper and Data Protection certificate must
be identical across relevant services; do not regenerate them on upgrade.

DB connection: `Host=postgres;Database=gatewaydb;Username=<role>;Password=<secret>;
SSL Mode=VerifyFull;Root Certificate=/run/certs/ca.pem;GSS Encryption Mode=Disable`.
Redis connection: `redis:6379,user=gateway,password=<secret>,ssl=true,sslHost=redis,abortConnect=false`.
`ca.pem` supplies trust for Redis/internal HTTPS (SSL_CERT_FILE); include the needed
municipal/intermediate trust, and maintain public CA trust for external providers.
Never disable certificate validation. The .NET chiseled images omit Kerberos libraries;
GSS encryption is disabled because verified TLS/SCRAM is the chosen authentication.

Data Protection uses a stable encryption certificate with private key, optionally a
password from file. Keep it separately backed up and tightly controlled. Demo
certificates are not production PKI. Database initialization creates three roles only
on an empty volume; the migration job applies schema and exact table/column permissions.
Changing password files alone does **not** change existing database role passwords.

Register `/signin-oidc` and `/signout-callback-oidc` for the admin public HTTPS origin,
code+PKCE, `roles` and `departmentCodes` claims. Test all three RBAC roles and department
scoping. IdP MFA and logout confirmation must be reviewed with users.

## Health and troubleshooting

Check container health and `/health/ready` with the appropriate CA. `/health/live` is
process liveness; `/version` reports assembly metadata. Authenticated Drift/Hälsa
shows gateway reachability/version and usage writer queue alongside DB/Redis checks.
An unreachable gateway must appear unhealthy/unknown, not as a fabricated version/zero queue.
OTel is opt-in through an approved collector endpoint; the hardening override deliberately
does not deploy/expose a public Aspire dashboard.

| Symptom | Action |
|---|---|
| Migration failed | Keep APIs stopped; check schema/role/TLS/secret permissions, inspect sanitized error type, correct and rerun migrator |
| Redis denied command | Confirm restricted ACL matches shipped store operations; never grant `+@all`, CONFIG, KEYS or FLUSHALL |
| TLS/health probe failed | SAN/CA/expiry/file ownership; include localhost SAN for in-container HTTPS probes |
| OIDC redirect/401/403 | Authority metadata, client/callback URLs, Secure cookies, role and department claims; do not dump tokens |
| 402 budget_exceeded | Inspect every department/team/key budget and rotation lineage; rotation does not reset spend |
| 429 | RPM/TPM or admin quota; respect Retry-After, do not remove limits to fix a test |
| Queue grows / DB outage | Restore DB connectivity; consider draining gateway traffic; bounded queue back-pressures callers |
| Requests go to an unexpected model | Check the `x-ume-rule` response header and the usage record's `RoutingRuleName`; use the routing rule dry run (`POST /api/routing-rules/test`) with the same headers, key and budget values; look for gateway warnings about ignored rules |
| Rules not applying after a reorganisation | A rule whose team/department was removed is *orphaned* and disabled until it is reassigned (`GET /api/routing-rules?orphaned=true`) |
| No providers | Residency/capability/enable/drain/circuit settings; inspect request_id metadata, not content |

## Backup and restore

Back up Postgres with `deploy\Backup-Database.ps1` using the exact running Compose
project and a protected destination. It executes `pg_dump` inside the DB container,
copies the binary archive (avoiding PowerShell 5.1 binary-pipeline corruption) and
cleans its exact temporary archive in the protected database data directory. Docker's
archive API cannot reliably copy tmpfs files; no shell binary pipeline is used.
No password appears on command arguments or output.
Backups contain metadata, HMACs, encrypted provider credentials and encrypted DP keys:
encrypt them, restrict access and apply the approved retention decision.

Use `deploy\Restore-Database.ps1` only with an explicit `-ConfirmRestore` after stopping
the APIs/migrator and selecting the correct project/backup. Restore replaces gatewaydb
objects. Re-run migrations and role grants before starting APIs. Role/bootstrap
passwords are separately managed, not stored in the database archive.

Back up the Data Protection private certificate/password and HMAC pepper separately
through the secret manager. Test restoration in an isolated environment: schema,
key authentication, decrypted provider credential, new BFF session and billing query.
A dump without the encryption certificate/pepper is not a complete recoverable system.
Define and measure RPO/RTO; the POC does not promise availability.

## Incidents and safe shutdown

For suspected key compromise revoke it, verify invalidation, examine request_id metadata
and rotate related credentials if needed. For unsafe provider routing drain/disable it
and restrict keys to approved on-prem targets. Preserve access/audit evidence under the
records policy; do not capture conversations to investigate. Coordinate municipal
cybersecurity reporting and GDPR breach assessment with the incident owner/DPO.

A routing rule that misroutes traffic: disable it (Routing > Routing rules > Disable, or `PUT /api/routing-rules/{id}` with `isEnabled: false`).
The change is audited and published to all gateway instances at once (the 30 s catalogue cache is only the fallback), and requests then go to the model they asked for.
Find the affected requests by `RoutingRuleName` in the usage records or the `x-ume-rule` response header; do not capture conversations. Rules that are disabled or have no owner are never applied.

Stop accepting traffic, allow usage to drain and stop the **exact** deployment without
`--volumes` for normal shutdown. Never use production `down --volumes`, `aspire destroy`
or `aspire stop --force` as a routine restart. The test script's volume deletion applies
only to its uniquely named disposable test project.
