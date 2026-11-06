# Network and ports

What listens where, who connects to it and what the services reach out to, for each supported setup: the reference an
infrastructure or security team needs to open ports and write firewall and proxy rules. Every number below is the
default in the file named next to it; where a value is not set anywhere in this repository the text says **not set by
the gateway; agree with infra**.

Two rules hold in every setup:

- **Everything is HTTPS or TLS.** The gateway, admin API and Data API listen only on HTTPS (no plain HTTP listener, no
  redirect from port 80); Postgres refuses connections without TLS (`hostnossl ... reject` in
  [`pg_hba.conf`](../deploy/postgres/pg_hba.conf)); Redis has no plain port (`port 0`, `tls-port 6379` in
  [`redis.conf`](../deploy/redis/redis.conf)). In production every provider address must be `https` or `wss`
  (a provider at `http://` is refused with `https_required`, `src/Ume.LlmGateway.Infrastructure/Providers/ProviderTransport.cs`).
- **Every app listens on 8443 inside its container**, and only the host side of the mapping is configurable
  ([`compose.prod.yaml`](../deploy/compose.prod.yaml), header comment). The health probe inside the container checks
  `https://localhost:8443`.

## Components

| Component | What it is | Container port | Default host port |
|---|---|---|---|
| Gateway | Data plane: `/v1` API for client applications (virtual keys), live audio over WebSocket | 8443 HTTPS | 8443 |
| Admin API | Control plane and the admin UI (served by the admin API itself), signs admins in through OIDC | 8443 HTTPS | 9443 |
| Data API (optional, profile `data`) | Read-only API for BI, management and SIEM integrations | 8443 HTTPS | 10443 |
| Migrations | One-shot job that migrates the database, then exits | none | none |
| Postgres | Database `gatewaydb` (and `keycloak` with the optional Keycloak project) | 5432 TLS | none |
| Redis | Rate limits, sessions, circuit state, invalidation | 6379 TLS | none |
| Reverse proxy | Yours (nginx, Nginx Proxy Manager, F5, an Ingress controller); terminates public TLS and re-encrypts | 443 | – |
| OpenTelemetry Collector (optional) | Yours; receives logs, traces, metrics and security events over OTLP | 4317 in the example | – |
| Keycloak (optional project) | `deploy/keycloak`: an OIDC provider next to the gateway | 8443 HTTPS, management 9000 (never published) | 11443 |
| Providers | Outside: Azure OpenAI / Foundry, OpenAI, Anthropic, Berget AI, on-prem Ollama or vLLM | – | – |
| Client systems | Outside: applications such as Ume.Rag, admins' browsers, Data API consumers | – | – |

```text
                     client apps (API keys)     admins' browsers          Data API consumers
                              │                       │   │                       │
                              │ HTTPS 443             │   │ HTTPS 443 (sign-in)   │ HTTPS 443 or 10443
                              ▼                       ▼   ▼                       ▼
                     ┌───────────────────────── reverse proxy :443 ──────────┐   IdP / Keycloak :443
                     │  llm.example.se     llm-admin.example.se   auth...    │        ▲     ▲
                     └──────┬───────────────────┬──────────────────┬─────────┘        │     │
               HTTPS 8443(+8444)        HTTPS 9443(+9444)     HTTPS 11443             │     │
                            ▼                   ▼                  ▼                  │     │
   Docker host  ┌────────────────┐  ops ┌───────────────┐   ┌──────────┐              │     │
                │ gateway        │◄─────│ admin API +UI │──►│ Keycloak │──────────────┘     │
                │ (edge,data,llm)│ 8443 │ (edge, data)  │   │ (option) │ (brokering, LDAPS) │
                └──┬───────┬─────┘      └──┬──────┬─────┘   └────┬─────┘                    │
                   │       │  TLS 6379     │      │ OIDC back-channel 443 ──────────────────┘
                   │       ▼               ▼      │      TLS 5432 │
                   │     Redis ◄───────────┘      ▼               ▼
                   │                         Postgres ◄──── Data API (option), migrations
                   │ HTTPS/WSS 443 (or the provider URL's port)
                   ▼
   providers: *.openai.azure.com, *.services.ai.azure.com, api.openai.com, api.anthropic.com, api.berget.ai, on-prem
```

The Compose networks ([`compose.prod.yaml`](../deploy/compose.prod.yaml)): `data` is `internal: true` (Postgres, Redis,
migrations, and the apps' database side; no route out), `edge` holds the three HTTPS apps, and `llm` is the gateway's
network for provider traffic. `edge` and `llm` are ordinary bridges, so the admin API and Data API can reach the
outside through `edge` as well (they need to, see *Outbound*). Docker networks are not an egress firewall
([architecture](architecture.md#components-and-trust-boundaries)): on one host every container leaves with the host's
address, so the host or network firewall enforces the destinations below.

## Inbound

| Listener | Host port (default) | Protocol | Who connects | Purpose | Required | Configured by |
|---|---|---|---|---|---|---|
| Reverse proxy, gateway host name | 443 | HTTPS; WebSocket upgrade on `/v1/realtime` and `/v1/realtime/translations` | Client applications with virtual keys | LLM API | Yes, with a proxy | Your proxy ([`nginx.conf.example`](../deploy/proxy/nginx.conf.example)) |
| Reverse proxy, admin host name | 443 | HTTPS | Admins' browsers | Admin UI and admin API, OIDC callbacks `/signin-oidc`, `/signout-callback-oidc` | Yes, with a proxy | Your proxy |
| Reverse proxy, Keycloak host name | 443 | HTTPS | Browsers of everyone who signs in; back-channel of the admin API and other systems | OIDC | Only with the Keycloak project | Your proxy ([keycloak.md](keycloak.md#set-up), step 4) |
| Gateway | 8443 | HTTPS (TLS from the internal CA or your PKI), WebSocket | The proxy; or client applications directly when there is no proxy | `/v1/*`, `/health/live`, `/health/ready`, `/version` | Yes | `UME_GATEWAY_BIND`, `UME_GATEWAY_PORT` |
| Gateway, second slot | 8444 | as above | The proxy only | Rolling updates | Only with `compose.rolling.yaml` | `UME_GATEWAY_B_PORT` |
| Admin API and UI | 9443 | HTTPS | The proxy; or admins' browsers directly | Admin UI, `/api/*`, `/bff/*`, OIDC callbacks, health | Yes | `UME_ADMIN_BIND`, `UME_ADMIN_PORT` |
| Admin API, second slot | 9444 | HTTPS | The proxy only | Rolling updates | Only with `compose.rolling.yaml` | `UME_ADMIN_B_PORT` |
| Data API | 10443 | HTTPS (always the internal CA's certificate) | Integrations (ETL, SIEM connector, BI) with client-credentials tokens or Data API keys; or your proxy / API gateway | `/v1/usage/*`, `/v1/security/*`, `/v1/catalog/*`, `/openapi/v1.json`, health | Only with `COMPOSE_PROFILES=data` | `UME_DATA_BIND`, `UME_DATA_PORT` |
| Keycloak | 11443 | HTTPS | The proxy only (Keycloak trusts `X-Forwarded-*`) | OIDC, Keycloak admin console | Only with the Keycloak project | `UME_KEYCLOAK_BIND`, `UME_KEYCLOAK_PORT` ([`init-keycloak.sh`](../deploy/keycloak/init-keycloak.sh) `--bind`, `--port`) |

**Bind address.** Without `--proxy`, `init-deployment.sh` binds every listener to `127.0.0.1`; with `--proxy` to
`0.0.0.0` ([`init-deployment.sh`](../deploy/init-deployment.sh): "Listeners default to loopback (0.0.0.0 with --proxy:
firewall them to the proxy only)"). [`compose.portainer.yaml`](../deploy/compose.portainer.yaml) defaults to `0.0.0.0`.
Keycloak's bind defaults to the admin API's (`UME_ADMIN_BIND`). Change it for all three apps with `--bind <address>`;
with the proxy on the same host, `--bind 127.0.0.1`.

**Reachable only from the proxy** when a proxy is in front: 8443, 8444, 9443, 9444 and 11443 (and 10443 if the Data API
is published through the proxy). Restrict them with the host firewall to the proxy's address
([runbook](runbook.md#reverse-proxy-and-pki)). Keycloak's port must never be reachable from anywhere else, because it
believes the `X-Forwarded-*` headers ([keycloak.md](keycloak.md#set-up)). The gateway believes `X-Forwarded-For` only from
addresses listed under *Settings > Trusted proxies* ([client addresses](client-addresses.md)).

**Health and monitoring.** `/health/live`, `/health/ready` and `/version` are on the same HTTPS listener as the API, without
authentication; they return only an aggregate status (`src/Ume.LlmGateway.ServiceDefaults/Extensions.cs`). There is no
metrics scrape endpoint: telemetry is pushed over OTLP (*Outbound*). A load balancer monitors `GET /health/ready` over
HTTPS and expects 200 ([updates without downtime](zero-downtime-updates.md#set-it-up)).

### What the reverse proxy must allow

From [`nginx.conf.example`](../deploy/proxy/nginx.conf.example) and the runbook's
[proxy requirements](runbook.md#reverse-proxy-and-pki):

| Setting | Gateway host | Admin host |
|---|---|---|
| Upstream | `https://<docker host>:8443` (and `:8444` with rolling) | `https://<docker host>:9443` (and `:9444`) |
| Upstream TLS | Re-encrypt. The upstream certificate is from the internal CA: skip verification or trust `deploy/certs/ca.pem` with `proxy_ssl_name gateway` | Same, `proxy_ssl_name adminapi` |
| Host header | Pass through unchanged | Pass through unchanged (the OIDC redirect URIs are built from it) |
| `X-Forwarded-For` | Append the client address; list the proxy under *Settings > Trusted proxies* | Append |
| WebSocket | Pass `Upgrade` and `Connection` (live audio, `/v1/realtime`) | Not needed |
| Buffering | Response buffering off (streamed tokens), request buffering off (no recordings in proxy temp files) | Default |
| Body size | `client_max_body_size 32m` in the example covers audio uploads (26 MB, `Gateway:MaxAudioRequestBodyBytes`); image edits allow 50 MB (`Gateway:MaxImageRequestBodyBytes`) and need 52 MB ([runbook](runbook.md#reverse-proxy-and-pki)). Raise both together | Default |
| Timeouts | `proxy_read_timeout` and `proxy_send_timeout` 600 s (long generations; the gateway pings live sessions every 30 s); `proxy_connect_timeout` 5 s | `proxy_connect_timeout` 5 s |
| Retry | `proxy_next_upstream error timeout non_idempotent`, within 10 s, 2 tries (rolling updates) | Same |
| Host names | Distinct host names per service: the proxy routes by name | |

Whether the proxy also listens on port 80 to redirect to 443 is **not set by the gateway; agree with infra** (the example
only listens on 443, and the apps never redirect).

## Outbound (egress)

Allow only what the deployment uses. All are TCP.

### Gateway

| Destination | Port / protocol | Purpose | When | Configured in |
|---|---|---|---|---|
| Each configured provider's base URL host | 443 HTTPS, or the port in the base URL | Model requests (streamed SSE) | For every provider an admin adds | Admin UI, *Providers* (base URL, stored in the database) |
| The same hosts | 443 WSS, or the port in the base URL | Live audio sessions (`/v1/realtime`), opened by the gateway to the provider | Only with realtime models | Same |
| Azure AI Foundry: `<resource>.services.ai.azure.com` as well as the entered `<resource>.openai.azure.com` or `<resource>.cognitiveservices.azure.com` | 443 | Claude on Foundry (`…/anthropic/v1`) and Foundry rerankers (`…/providers/<publisher>/v2`) go to the resource's `services.ai.azure.com` host, whatever host the admin entered | Foundry providers | `src/Ume.LlmGateway.Domain/Providers/FoundryEndpoint.cs` |
| Content guard model | Its provider's host and port | The guard model is an ordinary provider (usually on-prem) | Only with the content guard | Admin UI ([content guard](content-guard.md)) |
| Budget alert webhook | The URL's host and port | Budget alerts, one POST each, 5 s timeout | Only if set | `Gateway:AlertWebhookUrl` (`Gateway__AlertWebhookUrl`) |
| OpenTelemetry Collector | The endpoint's port (example 4317, OTLP/gRPC, TLS) | Logs, traces, metrics, security events | Only if set | `UME_OTEL_ENDPOINT` → `OTEL_EXPORTER_OTLP_ENDPOINT` |
| Redis, Postgres | 6379, 5432 | See *Internal only* | Always | Connection strings in the secret files |

**Rule for provider egress:** allow the gateway (on one Docker host: the host) **to each configured provider's base URL
host on 443** (or the port the base URL names), nothing else. Provider egress follows what admins configure, so review
it whenever a provider is added. Known hosts, as examples from the documentation:

| Provider | Host(s) | Source |
|---|---|---|
| Azure OpenAI / Azure AI Foundry | `<resource>.openai.azure.com`, `<resource>.cognitiveservices.azure.com`, `<resource>.services.ai.azure.com` (all three for one resource) | [providers and models](providers-and-models.md#reranking-optional), [live audio](live-audio.md) |
| Foundry Voice Live | `<resource>.services.ai.azure.com` (`/voice-live`) | [live audio](live-audio.md) |
| OpenAI | `api.openai.com` | [live audio](live-audio.md) |
| Anthropic | `api.anthropic.com` | `src/Ume.LlmGateway.MigrationService/DevSeeder.cs` (development seed) |
| Berget AI | `api.berget.ai` | [providers and models](providers-and-models.md#reranking-optional) |
| Ollama Cloud | `ollama.com` | `DevSeeder.cs`, [providers and models](providers-and-models.md#list-prices) |
| On-prem Ollama, vLLM, Speaches, a guard model | Your server's HTTPS address and port | Ports are the server's own: **not set by the gateway; agree with infra** |

Providers authenticate with an API key or bearer token in a header; the gateway does not fetch tokens from Entra ID or
an instance metadata service for providers (`ProviderAuthMode` in `src/Ume.LlmGateway.Domain/ProviderEnums.cs`).

### Admin API

| Destination | Port / protocol | Purpose | When | Configured in |
|---|---|---|---|---|
| OIDC provider (`Oidc:Authority`) | 443 HTTPS | **Back-channel:** discovery document, signing keys (JWKS), the token endpoint (authorization code with PKCE is redeemed by the admin API, not the browser) | Always | `UME_OIDC_AUTHORITY` → `Oidc__Authority` |
| Gateway operations endpoint | 8443 HTTPS, `https://gateway:8443` on the internal `edge` network | Health, version and usage-writer state for *Operations* (signed request) | Always | `Gateway__OperationsUrl` (`UME_GATEWAY_OPERATIONS_URL`; with PKI certificates the gateway's public URL, so the admin API must reach the proxy) |
| Each configured provider's host | 443 or the base URL's port | **Discover models** (lists the provider's models with its credential) | When an admin uses discovery | Admin UI, *Providers* |
| `models.dev` | 443 HTTPS | Model list prices, once a day | On by default; empty `Admin:PriceListUrl` turns it off, or point it at an internal copy | `Admin:PriceListUrl` (default `https://models.dev/api.json`) |
| `prices.azure.com` | 443 HTTPS | Azure Retail Prices API: suggested prices for Foundry deployments during discovery | When discovering Foundry models | Fixed host (`src/Ume.LlmGateway.AdminApi/Catalog/Pricing/AzureRetailPrices.cs`); region `Admin:AzurePriceRegion` |
| `api.github.com` | 443 HTTPS | Latest release, shown in *Settings > Version* | On by default | `Admin:CheckForReleases=false` turns it off; `Admin:ReleaseRepository` |
| OpenTelemetry Collector | As for the gateway | Telemetry, admin security events | Only if set | `UME_OTEL_ENDPOINT` |
| Redis, Postgres | 6379, 5432 | See *Internal only* | Always | Secret files |

An admin API without internet access works: set `Admin__PriceListUrl=` (or an internal copy), `Admin__CheckForReleases=false`,
and add Foundry prices by hand ([providers and models](providers-and-models.md#list-prices)).

### Data API

| Destination | Port / protocol | Purpose | When | Configured in |
|---|---|---|---|---|
| Token issuer (`DataApi:Authority`) | 443 HTTPS | **Back-channel:** discovery and JWKS to validate client-credentials tokens | When an authority is set (defaults to `UME_OIDC_AUTHORITY`); not needed with Data API keys only | `UME_DATA_AUTHORITY` → `DataApi__Authority` |
| Postgres | 5432 | Read-only role `ume_data` | Always | Secret file |
| OpenTelemetry Collector | As above | Telemetry, `data.read` events | Only if set | `UME_OTEL_ENDPOINT` |

The Data API does not use Redis ([`init-deployment.sh`](../deploy/init-deployment.sh): "read-only role, no pepper, no Redis").

### Keycloak (optional project)

| Destination | Port / protocol | Purpose | When |
|---|---|---|---|
| Postgres (`postgres:5432` on the gateway project's `data` network) | 5432 TLS, `verify-full` | Database `keycloak`, role `ume_keycloak` | Always |
| `login.microsoftonline.com` | 443 HTTPS | Back-channel to Entra ID: discovery, token endpoint, JWKS | Only when Entra ID is brokered |
| `<adfs host>` | 443 HTTPS | Back-channel to AD FS | Only when AD FS is brokered |
| Domain controllers | LDAPS (`ldaps://`; port 636 is the LDAPS standard, 3269 the global catalog over TLS) | User federation to Active Directory | Only with LDAP federation. The repository states only `ldaps://`: **port not set by the gateway; agree with infra** |

`realm-import` talks only to Keycloak (`https://keycloak:8443`) over the Keycloak project's own network.

### Browser-side flows

These are made by the admins' browsers, not by the servers, so they belong in the client network's rules, not the
gateway host's:

| From | To | Port | When |
|---|---|---|---|
| Admins' browsers | Admin host (proxy) or `<host>:9443` | 443 / 9443 | Always |
| Admins' browsers | The OIDC provider's authorization and end-session endpoints (`UME_OIDC_AUTHORITY`) | 443 | Sign-in and sign-out |
| Admins' browsers | Brokered provider (`login.microsoftonline.com`, the AD FS host), redirected to by Keycloak | 443 | Only when brokered |
| Data API integrations | The token endpoint of `UME_DATA_AUTHORITY` | 443 | Client-credentials tokens |

The admin UI loads nothing from other hosts: it is served by the admin API, whose Content-Security-Policy allows only
`'self'` for scripts, styles, fonts and `connect-src` (`src/Ume.LlmGateway.AdminApi/Program.cs`).

### The Docker host or cluster nodes

| Destination | Port | Purpose |
|---|---|---|
| `ghcr.io` (release images), `docker.io` (Postgres, Redis, `alpine/openssl` for Portainer, `keycloak-config-cli`), `quay.io` (Keycloak base image) | 443 | Image pulls, or your registry instead (`GATEWAY_IMAGE` and the other image variables). The registries' CDN hosts are **not set by the gateway; agree with infra** |
| DNS resolvers, time servers | 53, 123 | Name resolution for provider and IdP hosts; correct time for OIDC tokens and signed operations requests. **Not set by the gateway; agree with infra** |

## Internal only

Never open these on the host firewall. In Compose they are not published at all (no `ports:`; the hardening file even
`!reset`s them).

| Service | Port | Protocol | Clients | Access control |
|---|---|---|---|---|
| Postgres | 5432 | TLS required, SCRAM-SHA-256 | Migrations (`ume_migrator`), gateway (`ume_gateway`), admin API (`ume_admin`), Data API (`ume_data`), Keycloak (`ume_keycloak`, database `keycloak` only) | [`pg_hba.conf`](../deploy/postgres/pg_hba.conf); network `data` (`internal: true`) |
| Redis | 6379 | TLS only (`port 0`), ACL user `gateway`, `default` off | Gateway, admin API | [`redis.conf`](../deploy/redis/redis.conf), [`users.acl.example`](../deploy/redis/users.acl.example); network `data` |
| Gateway operations endpoint | 8443 | HTTPS, signed with the key pepper | Admin API (`https://gateway:8443`) | Network `edge` |
| Keycloak management | 9000 | HTTP | Keycloak's own health check inside the container | Never published ([`Dockerfile`](../deploy/keycloak/Dockerfile)) |
| Migrations | none | – | Connects to Postgres only, then exits | Network `data` |
| Portainer `bootstrap` | none | – | Writes certificates and secrets into volumes, then exits | Network `data` |

Postgres and Redis always use certificates from the internal CA, with `SSL Mode=VerifyFull` and `ssl=true,sslHost=redis` in
the connection strings ([`init-deployment.sh`](../deploy/init-deployment.sh)). `pg_hba.conf` accepts the roles from any
address; the `data` network (or, on Kubernetes, your NetworkPolicies) is what limits who can connect.

## Per setup

### (a) One Docker host with `compose.prod.yaml`

**Direct, no proxy** (`init-deployment.sh`): clients use `https://<host>:8443` and `https://<host>:9443`
([runbook](runbook.md#reverse-proxy-and-pki)), bound to `127.0.0.1` by default. To serve other machines, set `--bind` to
the host's address and open 8443 to client networks and 9443 to admins' networks. The certificates come from the internal
CA, so clients must trust `deploy/certs/ca.pem` unless you pass PKI certificates. Rolling updates are not possible
without a proxy.

**Behind a proxy** (`init-deployment.sh --proxy`): the listeners bind `0.0.0.0`; open 8443 and 9443 to the proxy only and
443 on the proxy to clients and admins. `init-deployment.sh` writes `deploy/proxy/nginx.conf`.

**Hardening.** `compose.prod.yaml` is already hardened. The older Aspire route, `deploy/generated/docker-compose.yaml` plus
[`compose.hardening.yaml`](../deploy/compose.hardening.yaml), publishes the same two ports (`UME_GATEWAY_*`, `UME_ADMIN_*`)
and removes the database and cache ports; it has no Data API.

**Rolling updates** ([`compose.rolling.yaml`](../deploy/compose.rolling.yaml), `init-deployment.sh --proxy --rolling`): a
second gateway (`gateway-b`, host port 8444) and admin API (`adminapi-b`, 9444) run next to the first, on the same bind
address. The proxy lists both ports per service and must reach all four; `rolling-update.sh` replaces one slot at a time
while the other serves. Both gateway slots carry the network alias `gateway`, so the admin API's
`https://gateway:8443` reaches whichever runs. The Data API keeps one slot and is down while it restarts
([`rolling-update.sh`](../deploy/rolling-update.sh)). Live audio sessions on a stopping slot end
([updates without downtime](zero-downtime-updates.md#what-is-still-interrupted)).

### (b) Portainer

[`compose.portainer.yaml`](../deploy/compose.portainer.yaml) is the same stack and the same ports (8443, 9443, 10443 with
the `data` profile), but **binds `0.0.0.0` by default** (`UME_GATEWAY_BIND`, `UME_ADMIN_BIND`, `UME_DATA_BIND`): firewall
the ports to the proxy ([runbook](runbook.md#portainer-and-other-hosts-without-shell-access)). The `bootstrap` container
has no network access beyond the internal `data` network. Rolling updates are not supported (Portainer recreates every
container at once), and PKI certificates for the public listeners are not supported.

### (c) Kubernetes

[`deploy/kubernetes`](kubernetes.md) ships:

| Object | Port | Notes |
|---|---|---|
| Service `gateway` | 8443 (`https`) | ClusterIP; the admin API reaches it as `https://gateway:8443` |
| Service `adminapi` | 8443 (`https`) | ClusterIP |
| Service `dataapi` | 8443 (`https`) | Only with `components/dataapi`; **no Ingress in the example** |
| Service `postgres`, `redis` | 5432, 6379 | Only with `components/datastores`; configured like Compose (TLS, roles, ACL) |
| Ingress `gateway`, `adminapi` ([`example/ingress.yaml`](../deploy/kubernetes/example/ingress.yaml)) | 443 at the controller → `https` on the pods | ingress-nginx annotations: `backend-protocol: HTTPS`, `proxy-body-size: 32m`, buffering off, read/send timeout 600 s (gateway) |

Kubelet probes `/health/live` and `/health/ready` on 8443 over HTTPS. **No NetworkPolicies are shipped**; if the namespace
enforces them, allow (derived from the flows above, not tested in this repository):

- Ingress controller → `gateway` and `adminapi` pods on 8443 (and `dataapi` if you expose it);
- `adminapi` → `gateway` on 8443;
- `gateway`, `adminapi` → `redis` on 6379; `gateway`, `adminapi`, `dataapi`, `migrations` → `postgres` on 5432;
- `gateway` egress to provider hosts (443), `adminapi` egress to the IdP, providers, `models.dev`, `prices.azure.com`,
  `api.github.com` (443), `dataapi` egress to its token issuer (443), all three to the OTLP collector, and DNS.

List the Ingress controller's pod network under *Settings > Trusted proxies* ([kubernetes.md](kubernetes.md#install), step 5).
The datastores component's Postgres accepts the roles from any pod address (its own copy of `pg_hba.conf`), so a
NetworkPolicy is what keeps other pods out.

### (d) The Keycloak project on the same host, or an external OIDC provider

**External OIDC provider** (Keycloak elsewhere, Entra ID, AD FS): nothing listens for it on the gateway host. The admin API
needs back-channel egress to `UME_OIDC_AUTHORITY` on 443, the admins' browsers need to reach it, and the Data API needs
egress to `UME_DATA_AUTHORITY` when it takes tokens. Authority URLs: `https://<keycloak>/realms/<realm>`,
`https://<adfs>/adfs`, `https://login.microsoftonline.com/<tenant>/v2.0`
([runbook](runbook.md#identity-provider-existing-keycloak-ad-fs-entra-id-active-directory)).

**The Keycloak project** ([keycloak.md](keycloak.md), [`deploy/keycloak/compose.yaml`](../deploy/keycloak/compose.yaml)):

- Keycloak listens on 8443 in the container, published on `UME_KEYCLOAK_BIND:UME_KEYCLOAK_PORT` (default 11443; the bind
  follows `UME_ADMIN_BIND`, so `0.0.0.0` in proxy mode). HTTP is off (`KC_HTTP_ENABLED=false`); management port 9000 is
  not published.
- The proxy forwards `https://auth.example.se` to it, re-encrypting, with `X-Forwarded-For`, `-Proto` and `-Host`. Open
  11443 to the proxy only.
- Keycloak joins the gateway project's `data` network (`ume-gateway_data`, `UME_GATEWAY_DATA_NETWORK`) to reach Postgres on
  5432; `pg_hba.conf` lets `ume_keycloak` into database `keycloak` only.
- The admin API reaches Keycloak by its public name (`UME_OIDC_AUTHORITY=https://auth.example.se/realms/ume`), so it goes
  out and back in through the proxy: the Docker host must be able to resolve that name and reach the proxy on 443. If the
  proxy's certificate comes from a private CA, add that CA to the services' trust store with `--ca-file`
  ([runbook](runbook.md#reverse-proxy-and-pki)).

### (e) Keycloak brokering Entra ID or AD FS, or federating Active Directory over LDAP

From [keycloak.md](keycloak.md#identity-providers-brokered-or-direct):

| Setup | Browser (admins) | Keycloak back-channel | Gateway host |
|---|---|---|---|
| Brokered Entra ID | `login.microsoftonline.com:443`; redirect URI `https://auth.example.se/realms/ume/broker/entra/endpoint` | `login.microsoftonline.com:443` (discovery, token, JWKS) | Nothing new |
| Brokered AD FS | `<adfs host>:443` | `<adfs host>:443` (`/adfs/.well-known/openid-configuration`) | Nothing new |
| LDAP federation to AD | Only Keycloak | Domain controllers over LDAPS (`ldaps://`, read-only bind account); 636, or 3269 for the global catalog: **agree with infra** | Nothing new |

With Entra ID or AD FS used **directly** (no Keycloak), the same hosts are the admin API's back-channel instead. The gateway
itself never talks LDAP: the runbook says "Do not talk LDAP directly"
([runbook](runbook.md#identity-provider-existing-keycloak-ad-fs-entra-id-active-directory)). The Entra ID and AD FS settings
are documented but not yet verified against a real tenant, and LDAP federation is tested against OpenLDAP, not Active
Directory ([keycloak.md](keycloak.md#identity-providers-brokered-or-direct)).

### (f) On-prem providers or cloud providers

- **Cloud** (Azure, OpenAI, Anthropic, Berget AI): egress from the gateway (and from the admin API, for discovery) to the
  provider's host on 443. Nothing inbound.
- **On-prem** (Ollama, vLLM, Speaches, a guard model): the gateway connects to the server's address, which in production
  must be HTTPS (or WSS for live audio): put a TLS-terminating proxy in front of an Ollama or vLLM that only speaks HTTP
  ([runbook](runbook.md#ownership-and-production-prerequisites)). Open the gateway host → server on that port. A server
  certificate from a private or municipal CA needs that CA in the services' trust store (`--ca-file`,
  [runbook](runbook.md#reverse-proxy-and-pki)). The server's port is its own: **not set by the gateway; agree with infra**.

### (g) A client platform on another host (for example Ume.Rag)

| From the client platform | To | Port | Why |
|---|---|---|---|
| Its back end | Gateway public URL (`UME_GATEWAY_PUBLIC_URL`; through the proxy, or `<gateway host>:8443` direct) | 443 / 8443 | Model calls with a virtual key, WebSocket for live audio |
| Its back end | Data API (through your proxy or API gateway, or `<host>:10443`) | 443 / 10443 | Only if it reads usage or catalogue data |
| Its back end | The OIDC provider (Keycloak project at `https://auth.example.se/realms/ume`, or yours) | 443 | Its own sign-in back-channel and, for the Data API, client-credentials tokens |
| Its users' browsers | The OIDC provider | 443 | Sign-in, when it shares the realm |

If the platform's calls must pass a key restricted to client addresses, the address the gateway sees is the platform's
egress address (or, behind the proxy, the one in `X-Forwarded-For`): see [client addresses](client-addresses.md).
Nothing on the platform host needs to be reachable from the gateway.

### Local development

Aspire (`src/Ume.LlmGateway.AppHost`) chooses its own ports for the dashboard, Keycloak, the fake LLM and the services. They
are for developers on their own machine only and are never opened in a firewall; see
[local development](local-development.md).

## Firewall and infrastructure request

Copy and fill in. One line per rule; remove what the setup does not use.

```text
# Inbound
ALLOW  <client networks>        -> <proxy>:443              TCP  HTTPS + WebSocket   gateway API (<gateway host name>)
ALLOW  <admin networks>         -> <proxy>:443              TCP  HTTPS               admin UI (<admin host name>)
ALLOW  <everyone who signs in>  -> <proxy>:443              TCP  HTTPS               Keycloak (<keycloak host name>), optional
ALLOW  <integration networks>   -> <proxy or gateway host>:<443 or 10443>  TCP HTTPS Data API, optional
ALLOW  <proxy>                  -> <gateway host>:8443,9443 TCP  HTTPS               upstreams
ALLOW  <proxy>                  -> <gateway host>:8444,9444 TCP  HTTPS               second slots, rolling updates only
ALLOW  <proxy>                  -> <gateway host>:11443     TCP  HTTPS               Keycloak, optional
DENY   any                      -> <gateway host>:8443-8444,9443-9444,10443,11443   everything else
(never publish 5432, 6379, 9000)

# Outbound from <gateway host> (or the gateway, admin API and Data API pods)
ALLOW  <gateway host> -> <provider host>:443                TCP  HTTPS/WSS   each configured provider, incl. all three
                                                                             Foundry host names of a resource
ALLOW  <gateway host> -> <on-prem model server>:<port>      TCP  HTTPS/WSS   on-prem providers, guard model
ALLOW  <gateway host> -> <OIDC authority host>:443          TCP  HTTPS       admin API and Data API back-channel
ALLOW  <gateway host> -> models.dev:443                     TCP  HTTPS       list prices (or Admin__PriceListUrl=)
ALLOW  <gateway host> -> prices.azure.com:443               TCP  HTTPS       Foundry price suggestions
ALLOW  <gateway host> -> api.github.com:443                 TCP  HTTPS       release check (or Admin__CheckForReleases=false)
ALLOW  <gateway host> -> <otel collector>:<4317>            TCP  OTLP/gRPC   optional
ALLOW  <gateway host> -> <alert webhook host>:<port>        TCP  HTTPS       optional
ALLOW  <gateway host> -> <registry>:443                     TCP  HTTPS       image pulls (ghcr.io, docker.io, quay.io or yours)
ALLOW  <gateway host> -> <DNS>:53, <NTP>:123                UDP/TCP          resolution, time
# With the Keycloak project
ALLOW  <gateway host> -> login.microsoftonline.com:443      TCP  HTTPS       brokered Entra ID
ALLOW  <gateway host> -> <adfs host>:443                    TCP  HTTPS       brokered AD FS
ALLOW  <gateway host> -> <domain controllers>:<636>         TCP  LDAPS       LDAP federation (port: agree)

# Proxy settings for <gateway host name>
WebSocket upgrade on; response and request buffering off; read/send timeout 600 s; body size >= 32 MB (52 MB for
50 MB image edits); X-Forwarded-For appended; Host preserved; upstream HTTPS (trust ca.pem or skip verification).
Then list <proxy> under Settings > Trusted proxies in the admin UI.
```

## Where each port is configured

Change a port in one place; the internal 8443 stays as it is.

| Port | Setting | File |
|---|---|---|
| Gateway host port 8443 | `UME_GATEWAY_PORT` (`--gateway-port`), bind `UME_GATEWAY_BIND` (`--bind`) | `deploy/.env` (written by `init-deployment.sh`); Portainer stack variables |
| Admin host port 9443 | `UME_ADMIN_PORT` (`--admin-port`), `UME_ADMIN_BIND` | same |
| Second slots 8444, 9444 | `UME_GATEWAY_B_PORT`, `UME_ADMIN_B_PORT` (default: the first port + 1) | `deploy/.env` |
| Data API 10443 | `UME_DATA_PORT`, `UME_DATA_BIND` | `deploy/.env`; Portainer stack variables |
| Keycloak 11443 | `UME_KEYCLOAK_PORT` (`init-keycloak.sh --port`), `UME_KEYCLOAK_BIND` (`--bind`) | `deploy/keycloak/.env` |
| Internal 8443 of every app | `Kestrel__Endpoints__Https__Url` and the probe's `UME_HEALTHCHECK_URL`; Kubernetes `containerPort`/Service `port` | Compose files; `deploy/kubernetes/base/*.yaml`. Keep at 8443 |
| Postgres 5432, Redis 6379 | Postgres default port; `tls-port` in `redis.conf`; the connection strings | `deploy/redis/redis.conf`; `deploy/secrets/<service>/ConnectionStrings__*` |
| Gateway operations URL | `UME_GATEWAY_OPERATIONS_URL` → `Gateway__OperationsUrl` (default `https://gateway:8443`) | `deploy/.env`; Kubernetes `ume-adminapi-config` |
| OTLP endpoint | `UME_OTEL_ENDPOINT` → `OTEL_EXPORTER_OTLP_ENDPOINT`; Kubernetes ConfigMap literal | `deploy/.env`, overlay |
| Proxy upstreams | Generated from the ports above | `deploy/proxy/nginx.conf` (from `nginx.conf.example`) |
| Provider hosts and ports | The provider's base URL | Admin UI (database), not a file |
