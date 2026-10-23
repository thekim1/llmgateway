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

Download a release bundle (*Releases* below), or build and push the images yourself (*Build and
publish*), then run `deploy/init-deployment.sh` on the Docker host and
`docker compose -f deploy/compose.prod.yaml --env-file deploy/.env up -d --wait`.
The script generates everything in *Nonsecret deployment inputs* and *Certificate and secret
layout* below (internal CA, certificates, secrets, `deploy/.env`) and prints the OIDC redirect
URIs to register. It never overwrites existing secrets; `--renew-certs` re-issues service
certificates (generated leaves last 825 days, the CA and Data Protection certificate 10 years).
`deploy/certs/ca.key` signs them: move it off the host after setup and bring it back only for
`--renew-certs` (anyone who can read it can issue certificates the services trust). `--renew-ca`
replaces the internal CA and re-issues its certificates; run it once on installations set up before
the CA got its `keyUsage` extension (strict clients such as Python 3.13 refuse such a CA), then
restart the stack.
For municipal PKI, replace the generated certificate files (same names) before starting.
The sections below describe the same layout for operators who provision it by other means,
and the Aspire-generated base plus `compose.hardening.yaml`.

## Portainer and other hosts without shell access

`deploy/compose.portainer.yaml` is the same hardened stack configured **only through environment
variables**, for operators who cannot run `init-deployment.sh` (Portainer, managed Docker hosts).
Paste it into a Portainer stack (Web editor) or deploy the repository's file via Git, and set the
variables listed at the top of the file (images, `UME_OIDC_AUTHORITY`, `UME_GATEWAY_HOST`,
`UME_GATEWAY_PUBLIC_URL`; optional claim mapping and ports). The images must be in a registry the
host can pull from. The `compose.portainer.yaml` attached to a GitHub release already points at that
release's images (by digest), so the image variables are optional there.

A one-shot `bootstrap` container creates the internal CA, certificates, passwords, key pepper and
Redis ACL in the `ume-certs` and `ume-secrets` volumes on first start; the services still read them
as read-only files, never as environment variables. It is idempotent: secrets are never regenerated,
service certificates are re-issued when they expire within 30 days, the hostnames change or the CA changed
(restart the stack to renew; redeploying runs bootstrap again). An internal CA without the `keyUsage` extension
(created before it was added) is replaced automatically and all certificates are re-issued. The CA key stays in the
`ume-certs` volume because bootstrap needs it; restrict who can read Docker volumes on the host. `UME_OIDC_CLIENT_SECRET`, if used, is the one secret
you enter yourself; it is stored in the Portainer stack's environment, so restrict who can view it.

- **Back up** `ume-secrets/.pepper` and `ume-certs/data-protection.pfx` (for example
  `docker run --rm -v <stack>_ume-secrets:/s alpine cat /s/.pepper`). Deleting the stack's volumes loses
  them, and a database restored without them is unusable.
- Ports default to `0.0.0.0`; behind Nginx Proxy Manager or another proxy follow *Reverse proxy and PKI*
  (two hostnames, HTTPS upstreams) and firewall the ports to the proxy.
- PKI-issued certificates are not supported by this variant; use `compose.prod.yaml` with
  `init-deployment.sh` for that.
- **Following the newest release.** Each release also tags its images `:latest` (pre-releases do not), so the image
  variables can be `ghcr.io/<owner>/<repo>/gateway:latest` (and `adminapi`, `migrations`, `dataapi`) instead of a version.
  To upgrade, read the [upgrade guide](upgrade-guide.md) for the new version, then **Update the stack** with **Re-pull image
  and redeploy** switched on; *Settings > Version* in the admin UI says when a new release is out. Without the re-pull,
  Portainer keeps the `:latest` it pulled before. To roll back or hold a version, set the variables to that version's tag.
  Keep all four on the same tag: the migrations image must match the services.
- `compose.portainer.yaml` is generated: edit `compose.portainer.template.yaml`,
  `portainer/bootstrap.sh` or the postgres/redis config files, then run `deploy/build-portainer-stack.sh`
  (CI fails if the generated file is stale).

## Identity provider (existing Keycloak, AD FS, Entra ID, Active Directory)

The bundled Keycloak realm (`dev/keycloak`) is for local runs only; **production always uses an
existing OIDC provider** (`UME_OIDC_AUTHORITY` in `deploy/.env`). The admin API needs a client
(authorization code + PKCE; redirect URIs `https://<admin host>/signin-oidc` and
`/signout-callback-oidc`) and these
claims **in the ID token** (step-by-step setup per provider and what each role may do:
[Identity provider and roles](identity-and-roles.md)):

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

## Updates without downtime and Kubernetes

A single Compose stack is briefly down during an update. To avoid that, run two slots of the gateway and admin API
(`compose.rolling.yaml`, `init-deployment.sh --rolling`) behind your proxy and update with `deploy/rolling-update.sh`;
see [Updates without downtime](zero-downtime-updates.md). On Kubernetes, rolling updates are built in:
[Kubernetes](kubernetes.md). Check the release's *Rolling update* line in the [upgrade guide](upgrade-guide.md) first.

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
- Send the client address in `X-Forwarded-For` (`proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;`,
  the default in Nginx Proxy Manager) and list the proxy's address under **Settings > Trusted proxies** in the admin
  UI. Without that, every request appears to come from the proxy: keys restricted to client addresses are refused
  and refused credentials are recorded with the proxy's address. See *Client addresses and trusted proxies* below.
  (No ASP.NET Core forwarded-header setting is involved; scheme and host need no forwarding because the upstream hop is HTTPS.)
- Gateway host: disable response buffering and allow long reads (streamed completions can run
  for minutes), and allow bodies of a few MB or more: 32 MB covers the 26 MB audio upload limit
  (`Gateway__MaxAudioRequestBodyBytes`; raise both together), 52 MB the 50 MB of image edits (`Gateway__MaxImageRequestBodyBytes`). Disable request buffering as well, so uploaded
  recordings and documents are not written to the proxy's temporary files. Pass the WebSocket upgrade for live
  audio (`/v1/realtime`): the `Upgrade` and `Connection` headers, as in the nginx example; the gateway pings every
  30 s, so the 600 s read timeout does not cut a quiet session.
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
| Websockets Support | **on** (live audio, `/v1/realtime`) | off |
| Advanced (custom nginx configuration) | `proxy_buffering off; proxy_request_buffering off; proxy_read_timeout 600s; proxy_send_timeout 600s; client_max_body_size 32m;` | (none) |

### Client addresses and trusted proxies

A key can be restricted to client addresses (admin UI: key form, *Allowed client addresses*; one IP address or CIDR
network per line). The gateway compares them with the client address, which is the TCP connection's address unless the
connection comes from a **trusted proxy** (admin UI: *Settings > Trusted proxies*, gateway admins only). Then the gateway
reads `X-Forwarded-For` from the right, skips addresses that are themselves trusted proxies, and takes the first one that
is not as the client. Entries further left are never believed, because a client can put anything there.

- **List every proxy hop you operate**, for example both a load balancer and the nginx behind it. Find the address the
  gateway sees: the Settings page shows the address the admin API received the page request from, which is the proxy when
  the admin UI goes through the same proxy; otherwise look at *Source address* of refused credentials (Data API
  `security/auth-failures`, or the `gateway.auth.failed` security event).
- **Never trust addresses that clients also use.** A trusted address can claim to be any client. Trusting a whole office
  network, or `0.0.0.0/0` (refused by the admin API), would let anyone in it get past every key's address restriction.
- **Docker networks:** a proxy container on the same Docker network reaches the gateway from its container address, and a
  proxy on the host through the bridge's gateway address (often `172.17.0.1` or `172.18.0.1`), not the host's LAN address.
- **Changes apply within seconds** on every gateway instance (the admin API publishes a configuration change). A wrong list
  only affects keys with an address restriction; they are refused with `403 address_not_allowed`. Keys without a restriction
  keep working whatever the list holds.
- **IPv4 and IPv6:** an IPv4 client reaching a dual-stack socket is matched against IPv4 entries (`::ffff:192.0.2.1` counts as
  `192.0.2.1`). If clients may arrive over IPv6, list their IPv6 networks too.
- **Not a replacement for network controls.** Address restrictions limit where a leaked key works; they do not authenticate
  the caller. Keep the firewall rules on 8443/9443 described above.
- **Source address logging.** `Gateway:Security:SourceAddress` (truncated by default) applies to the recorded address only;
  the allow-list check always uses the full address in memory.

**PKI certificates.** Pass the certificate chain and key for each public name (the
leaf certificate file should include intermediates). `--ca-file` appends your root and
intermediates to the services' trust store, which also needs to cover the IdP and
approved providers if those use a private CA (`SSL_CERT_FILE` replaces the system store).
Because a PKI certificate seldom covers the internal name `gateway`, the admin API then reaches
the gateway's operations endpoint through its public URL (`UME_GATEWAY_OPERATIONS_URL`
in `deploy/.env`): the container must be able to resolve and reach that name. Postgres and Redis
always keep internal certificates. PKI-supplied certificates are not touched by `--renew-certs`;
replace the files (or re-run with the options) before they expire and restart the services.

## Releases (GitHub)

Pushing a tag `vX.Y.Z` runs `.github/workflows/release.yml`, which builds with `-p:Version=X.Y.Z` from the tag
(`<Version>` in `Directory.Build.props` stays the local placeholder). Before tagging, move the CHANGELOG's *Unreleased*
notes under `## X.Y.Z - date`, which become the release notes. The workflow:

1. runs the full verify workflow (build, tests, UI, audits, SBOM, Compose validation);
2. pushes `ghcr.io/<owner>/<repo>/gateway`, `adminapi` (with the admin UI), `migrations` and `dataapi`
   tagged `X.Y.Z` (linux/amd64, never `latest`);
3. builds the deploy bundle with `deploy/build-release-bundle.sh`, which pins the image defaults in
   `compose.prod.yaml` and `compose.portainer.yaml` to those digests;
4. boots the bundle with `init-deployment.sh` and `compose.prod.yaml` (data profile included) as a smoke test;
5. creates the GitHub release with `ume-llm-gateway-X.Y.Z.tar.gz`, `compose.portainer.yaml`, the SPDX SBOM
   and `SHA256SUMS`. A tag with a suffix (`v1.0.0-rc.1`) becomes a pre-release.

```bash
git tag v0.2.0 && git push origin v0.2.0
```

On the Docker host:

```bash
curl -fsSLO https://github.com/<owner>/<repo>/releases/download/v0.2.0/ume-llm-gateway-0.2.0.tar.gz
tar -xzf ume-llm-gateway-0.2.0.tar.gz && cd ume-llm-gateway-0.2.0
./deploy/init-deployment.sh
docker compose -f deploy/compose.prod.yaml --env-file deploy/.env up -d --wait
```

GHCR packages are private when first pushed. Either make the four packages public (package settings on
GitHub) or log the host in with a token that has `read:packages`:
`echo <token> | docker login ghcr.io -u <user> --password-stdin`. `GATEWAY_IMAGE`, `ADMINAPI_IMAGE`,
`MIGRATIONS_IMAGE` and `DATAAPI_IMAGE` in `deploy/.env` still override the pinned images (for example to
mirror them into an approved private registry). To upgrade, extract the new bundle next to the old one,
move `deploy/.env`, `deploy/certs` and `deploy/secrets` across, read the upgrade guide, and run the same
`up -d --wait`.

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
dotnet publish src\Ume.LlmGateway.DataApi -c Release /t:PublishContainer   # optional Data API
```

Locally built images are tagged with `<Version>` from `Directory.Build.props`. Push to the approved private registry and deploy
by immutable digest, not `latest`. .NET base and Postgres/Redis image digests are pinned (the .NET base digest in `Directory.Build.targets`).
Scan dependency/image reports and generate/review an SBOM per release (see upgrade guide).
No production `aspire prepare/deploy` secret-resolution file is required: this procedure
uses publish placeholders and operator-mounted secret files, not resolved `.env` secrets.

Always compose both files:
`docker compose -p ume-gateway -f deploy\generated\docker-compose.yaml -f deploy\compose.hardening.yaml config --quiet`.
Then run the same command with `up -d --wait --wait-timeout 180` instead of `config --quiet`.
Use host-native paths on Linux; the PowerShell examples are for the Windows build host.

## Nonsecret deployment inputs

Set `GATEWAY_IMAGE`, `ADMINAPI_IMAGE`, `MIGRATIONS_IMAGE` (and `DATAAPI_IMAGE` with the Data API) to approved `repository@sha256:...`.
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
| certs/dataapi | ca.pem, dataapi.crt/key (SAN dataapi, localhost and the Data API host); no Data Protection certificate |
| secrets/postgres | bootstrap_password, migrator_password, gateway_password, admin_password, data_password (Data API) |
| secrets/redis | users.acl based on users.acl.example, containing password SHA256 hash, not plaintext |
| secrets/migrations | ConnectionStrings__gatewaydb (ume_migrator), Security__KeyPepper, optional DataProtection__CertificatePassword |
| secrets/gateway | ConnectionStrings__gatewaydb (ume_gateway), ConnectionStrings__redis, Security__KeyPepper, optional DataProtection__CertificatePassword |
| secrets/adminapi | ConnectionStrings__gatewaydb (ume_admin), ConnectionStrings__redis, Security__KeyPepper, Oidc__ClientSecret if confidential client, optional DataProtection__CertificatePassword |
| secrets/dataapi | ConnectionStrings__gatewaydb (ume_data) only: no pepper, no Redis |

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
on an empty volume; the migration job applies schema and exact table/column permissions
(`deploy/postgres/app-roles.sql`, revoked and granted again on every run): the gateway role reads only
what it routes and accounts with (not the audit log or Data API keys) and cannot add Data Protection
keys (the admin API creates and rotates them); the admin role cannot change `UsageRetainedFrom`, which
only the retention function moves.
Changing password files alone does **not** change existing database role passwords.

Register `/signin-oidc` and `/signout-callback-oidc` for the admin public HTTPS origin,
code+PKCE, `roles` and `departmentCodes` claims. Test all three RBAC roles and department
scoping. IdP MFA and logout confirmation must be reviewed with users.

## Data API (optional)

The read-only Data API serves BI, management and security integrations over HTTPS; design and endpoints are in
[data-access.md](data-access.md). It runs as its own service with its own database role (`ume_data`, `SELECT` only,
and column by column on tables that hold secrets), so it can be deployed in a different network zone or left out.

1. **Start it.** `init-deployment.sh` and the Portainer bootstrap already create its certificate, password and
   connection string. Set `COMPOSE_PROFILES=data` (in `deploy/.env` or the Portainer stack) and `DATAAPI_IMAGE`, then
   `docker compose ... up -d --wait`. It listens on `UME_DATA_BIND:UME_DATA_PORT` (loopback:10443; 0.0.0.0 with
   `--proxy` or Portainer). Its certificate comes from the internal CA; for one from your PKI, put the Data API
   behind your proxy or API gateway. Existing installations need one superuser step first, see the upgrade guide.
2. **Register clients at your identity provider**, one per integration (an ETL job, a SIEM connector), using
   OAuth 2.0 client credentials. Each token needs:
   - `aud` = `ume-data-api` (`UME_DATA_AUDIENCE`). Keycloak: an *Audience* mapper; Entra ID: the application ID URI of
     an app registration that represents the Data API.
   - The permissions it may use, as values of `scope`, `scp` or `roles`: `usage.aggregate` (management dashboards),
     `usage.detail` (data warehouse), `security.read` (SIEM), `catalog.read` (dimensions). Keycloak: realm roles
     mapped to a `roles` claim (see the `ume-data-dev` client in `dev/keycloak/ume-realm.json`); Entra ID: app roles
     granted to the client as application permissions.
   - Tokens are accepted from `UME_DATA_AUTHORITY` (defaults to `UME_OIDC_AUTHORITY`).
   Alternatively (or for integrations that cannot do client credentials) a gateway admin creates a **Data API key** in
   the admin UI (*Access → Data API access*). Set `UME_DATA_PUBLIC_URL` (admin API `DataApi__PublicUrl`) to the
   Data API's public address so the UI can show a ready-made example request.
3. **Settings** (`DataApi__*` in the service environment; the Compose files expose `UME_DATA_AUTHORITY`,
   `UME_DATA_AUDIENCE`, `UME_DATA_MINIMUM_GROUP_SIZE` and `UME_DATA_TIME_ZONE`): `MinimumGroupSize` (5; departments with fewer active keys in a period
   are reported without their department), `TimeZone` (Europe/Stockholm; day and month boundaries), `SettleSeconds`
   (60), `MaxPageSize` (10 000), `RequestsPerMinute` per client (600), `Detail__IncludePiiCategories` (false), `AllowKeys` (true; Data API keys),
   `KeyCacheSeconds` (30; how long a revoked key may still work).

Every read is logged as a `data.read` security event (client, path, status, cursor), so the SIEM sees who pulled what.

## Security events and SIEM

The gateway and admin API write security events (refused keys, PII actions, policy refusals, admin changes) as
log records in the category `Ume.LlmGateway.Security`; event names and fields are listed in
[data-access.md](data-access.md#phase-1-security-events). To ship them:

1. Run an OpenTelemetry Collector (contrib distribution) that the services can reach. Start from
   `deploy/otel/collector-siem.example.yaml`: it keeps only the security category and sends it to syslog
   (RFC 5424 over TLS), with Splunk HEC, Elastic and Azure Monitor (Sentinel) as commented alternatives.
2. Set `UME_OTEL_ENDPOINT` (for example `https://otel-collector.internal:4317`) in `deploy/.env` or the Portainer
   stack. Both services then export logs, traces and metrics over OTLP; when it is empty nothing is exported.
   `SSL_CERT_FILE` points the services at the internal CA bundle, so the collector's certificate must chain to a CA in
   `ca.pem`.
3. Check that events arrive: a request with a made-up key produces a `gateway.auth.failed` event within
   `Gateway:Security:AuthFailureFlushSeconds` (10 s).

Push over OTLP is best-effort. The durable record is in the database: `AuthFailures` (refused keys, counted per
reason, endpoint, key and client network), `UsageRecords` (PII actions and refusals) and `AuditLog`.

`UME_SOURCE_ADDRESS` (`Truncated` by default, `Full` or `None`) sets how much of the client address is kept for
refused keys; record the choice in your DPIA. Behind a reverse proxy the gateway sees only the proxy's address until the
proxy is listed under *Settings > Trusted proxies* (see *Client addresses and trusted proxies*). The gateway ignores
`ASPNETCORE_FORWARDEDHEADERS_ENABLED`, which would believe `X-Forwarded-For` from any caller.

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
| Gateway readiness Degraded, check `exchange-rate` | No USD exchange rate is in effect, so costs use `Gateway:FallbackSekPerUsd` (default 10); enter the rate under Settings (`PUT /api/settings/exchange-rate`) |
| 500 `internal_error` from the gateway | Unexpected failure after acceptance; the request is in the usage records (outcome `ProviderError`, error `internal_error`, nothing charged). Find the logged exception type by `request_id` |
| Provider shows Degraded or Down but answers | The status covers only the last hour and only provider errors (5xx, timeouts, broken streams), so it clears an hour after a burst. Read the reason next to the status ("7 of 40 requests failed…"), then **Operations > Provider stability** for when it started; find the failed requests in Usage (outcome `ProviderError`, status 5xx, error code e.g. `stream_interrupted`) |
| p95 latency of several seconds | `latencyMs` is the whole request including fallback attempts and the full stream, so long streamed answers raise it. Compare p50/p95 per hour on **Provider stability**, and the provider's fallback rate |
| Log "provider … asked to retry … after … ms; it goes last until then" | The deployment answered 429/503 with `Retry-After` and is on cooldown (other deployments take its traffic); raise its quota or add a deployment at the same priority. `CircuitBreaker__MaxRetryAfter` caps the cooldown (default `00:05:00`, `00:00:00` turns it off) |
| 429 "already has N requests in progress" | The key's *Requests at once* limit (`maxConcurrentRequests`); raise it, or have the client queue. A slot that is never released (a crashed instance) expires after 15 minutes |
| 400 `context_length_exceeded` | The prompt is larger than the context window of every model it could go to (the model's *Context window*); route long prompts to a larger model, or correct a wrong context window |
| Requests take a little longer when a provider blips | The last candidate is retried once after a connection error or 502/503/504 (log "retrying provider … in … ms"); `Gateway__Retry__MaxRetries=0` turns it off |
| Log "Failed to write … usage records" and then "Wrote … journaled usage records" | Postgres refused writes for a while; the batches waited in the usage journal (Redis) and were written when it was back. *Operations* shows `journaledBatches` meanwhile. Nothing to do unless it stays above 0 |
| Log "Usage journal is full" | A long database outage filled the journal (`Gateway__UsageJournal__MaxRecords`, default 1 000 000). The writer now holds batches in memory; requests start to wait and, after 10 s, records are dropped. Restore Postgres; raise the cap only if Redis has the memory (about 1 KB per record) |
| Admin sign-in answers 500, log "Request failed (RedisServerException)" right after "Admin … signed in" | The Redis ACL lacks `setex psetex` (admin sessions are written with a time to live): redeploy with the updated `deploy/redis/users.acl.example` |
| Log "Usage journal unavailable (… NOPERM …)" | The Redis ACL lacks `zrangebyscore` (needed to take over a stopped instance's batches): redeploy with the updated `deploy/redis/users.acl.example` |
| 402 "token quota … is used up" | A token quota (Budgets, unit Tokens) is exhausted for the period; raise it, or let the request use models of another residency |
| 400/502 `content_blocked` | The key's content guard (policy Block) flagged the request or answer; the request in *Usage* shows what (*Content guard*). Set the key to Record to observe instead |
| Log "Content guard model … is not an enabled on-prem model" | `Gateway__ContentGuard__Model` names a model that is missing, disabled or not on an on-prem provider; the guard uses its rules only (see docs/content-guard.md) |
| Log "Content guard model unavailable …" / usage flag `guard_unavailable` | The guard model failed or took longer than `TimeoutMs`; check the on-prem server. With `FailOpen=false`, Block keys get 503 meanwhile |
| Provider keeps failing but its circuit stays closed | Upstream 401/403/404, `https_required` and `credential_unavailable` are configuration errors: requests fall back but the circuit breaker ignores them. Fix the credential, URL or model name |
| A service exits at startup with an options validation error | A required or out-of-range setting, e.g. `Oidc:Authority` missing, `Admin:RequestsPerMinute` outside 1–10000, `Gateway:BaseUrl`/`OperationsUrl` not an absolute http(s) URL (admin API), or invalid `Seed:*` settings (migration service) |
| Budget alert webhook not received | Each alert is posted once (5 s timeout, no retries) and is still stored (`GET /api/alerts`); a warning is logged when the post fails or the 1 000-alert queue is full |
| Live audio: 400 "requires a WebSocket connection" | The proxy drops the `Upgrade` header; see *Reverse proxy and PKI* |
| A successful request is missing from the gateway log | Only refused, failed and cancelled requests are logged at Information ("Request … key … status … in … ms"); successful ones are in the usage records (find them by `request_id`). To log every request, turn on **Settings > Request logging** (24 hours or no time limit; applies within seconds). Without the admin UI, set `Logging__LogLevel__Ume.LlmGateway.Gateway.Pipeline.GatewayRequestHandler=Debug` on the gateway and restart it. Either costs 6–14 % of peak throughput while on |
| Live audio: warning "session registry unavailable" | The Redis ACL lacks `zadd zrem zcard zremrangebyscore` (upgrade guide); sessions work but the per-key limit is off |
| Live audio: sessions end with `gateway_shutting_down` | Expected on deploy/restart: open sessions are closed with an error event and clients reconnect. Metrics: `ume.gateway.realtime.sessions` (open now), `ume.gateway.realtime.session.duration`, `ume.gateway.audio` (seconds) |

## Data retention

Old data is removed every night at 03:00 Europe/Stockholm by the admin API (several instances may run; they take turns).
How long data is kept is set by a gateway admin under *Settings > Data retention*:

| Data | Setting (default) | Removed | Kept |
|---|---|---|---|
| Usage records, refused-key records, budget alerts of periods that ended over a year ago | Usage data, 365 days (90–3 650) | Whole Europe/Stockholm months older than the setting | Monthly totals per key, team, department, model, provider and endpoint (`UsageMonthlyTotals`) |
| Admin audit log | Audit log, 365 days (90–3 650), or for ever | Entries older than the setting | Nothing |

- **Set both from your records-retention decision (gallringsbeslut) before going live.** Removal cannot be undone
  except by restoring a backup; backups keep removed rows until they expire, so align backup expiry too.
- **What still works for removed months:** budgets (a yearly budget reads the totals of its earlier months), the usage
  summary and CSV export (whole months; by day one row per month), Data API monthly aggregates. **What does not:** the
  request list, PII incidents, request lookup by id, the Data API daily aggregate and feeds.
- **The database does the removal.** The admin API calls `ume_retention_step(batch)` until it returns 0. The function runs
  with the schema owner's rights and treats anything below 90 days as 90; `ume_admin` may execute it but has no `DELETE`
  on these tables, so the audit log stays append-only for recent entries even if the admin API's credentials leak.
- **Check it ran:** *Settings > Data retention* shows the last run, rows removed, an error if it failed, the oldest kept
  usage and the next run; *Run now* starts one (audited as `run Settings`). The admin API logs
  `Data retention run removed N rows` or `Data retention run failed ...`. A failed run continues the next night.
- **Configuration** (admin API, `Retention__*`): `Enabled` (true; false stops the nightly run, *Run now* still works),
  `RunAt` (`03:00`, local time), `BatchSize` (5 000 rows per transaction, 100–50 000).
- **Large first run:** an existing database with years of usage removes it at the first run, in batches of `BatchSize`;
  run it outside office hours or start it with *Run now* at a quiet time. Postgres reuses the freed space; run
  `VACUUM (FULL) "UsageRecords"` in a maintenance window only if the disk must shrink.

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
