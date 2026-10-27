# Production setup

On a Linux Docker host (Docker 28+, Compose v2.24+) with an OIDC client for the admin UI, download a
[GitHub release](https://github.com/thekim1/llmgateway/releases) bundle; its Compose files point at
that release's images on GHCR, pinned by digest:

```bash
curl -fsSLO https://github.com/thekim1/llmgateway/releases/download/vX.Y.Z/ume-llm-gateway-X.Y.Z.tar.gz
tar -xzf ume-llm-gateway-X.Y.Z.tar.gz && cd ume-llm-gateway-X.Y.Z
./deploy/init-deployment.sh        # asks for the OIDC authority and public hostnames
docker compose -f deploy/compose.prod.yaml --env-file deploy/.env up -d --wait
```

Releases are made by pushing a tag `vX.Y.Z` that matches `<Version>` in `Directory.Build.props`; the
release workflow tests, pushes the images, smoke-tests the bundle and publishes it (runbook, *Releases*).
GHCR packages start private: make them public or `docker login ghcr.io` on the host. To build the images
yourself instead, see the runbook, *Build and publish*, and run the same commands from a clone.

`init-deployment.sh` creates an internal CA, service certificates, the Data Protection
certificate, all database/Redis passwords, the key pepper and `deploy/.env`
(non-secret settings, `GATEWAY_IMAGE`/`ADMINAPI_IMAGE`/`MIGRATIONS_IMAGE` overrides).
It is safe to re-run: existing secrets are never overwritten. All options are available
non-interactively (`--help`); `--renew-certs` re-issues the service certificates. Back up
`deploy/secrets/.pepper` and `deploy/certs/data-protection.pfx` separately from database dumps.

## Sizing

For a quiet installation, up to about 50 messages per second with room for bursts. Measured with PII detection and the
content guard on ([performance](performance.md#a-small-host-benchmarkscomparesizingrunsh)): the gateway carried
100 streamed messages/s in 0.5 vCPU and 236 MB without adding latency.

| | CPU | Memory | Disk |
|---|---|---|---|
| Gateway | 0.5 vCPU, up to 1 | 256 MB, up to 512 MB | – |
| Admin API and UI | 0.25 vCPU | 256 MB, up to 512 MB | – |
| Postgres | 0.5 vCPU, up to 1 | 512 MB, up to 1–2 GB | Usage data, below |
| Redis | 0.1 vCPU | 64 MB, up to 256 MB | None (no persistence needed) |
| **One host for everything** | **2 vCPU** | **4 GB** (with the OS) | **20 GB + usage data** |
| **Gateway and admin API only** (your own Postgres and Redis) | **1–1.5 vCPU** | **1.5 GB** | **5 GB** |

At 50 messages/s your own Postgres gets about 50 inserts per second (in batches, from a background writer) and your
own Redis about 450 commands per second, using a few MB.

Disk grows with usage records: about 560 bytes per request, kept for the retention period (365 days by default,
*Settings > Data retention*). Size by the average rate over the day, not the peak:

| Average rate | Per year |
|---|---|
| 5 messages/s (peaks of 50 in office hours) | about 90 GB |
| 10 messages/s | about 180 GB |
| 50 messages/s around the clock | about 890 GB |

The admin API was not measured under load; its figures are estimates. A guard model for the content guard runs on your
own inference server and is not included. For two gateway instances ([updates without downtime](zero-downtime-updates.md)),
add one gateway row.

## Other set-ups

No shell access (**Portainer**)? Use `compose.portainer.yaml` (attached to each release, image references
already pinned): the same stack configured only through environment variables, with certificates and secrets generated automatically in Docker volumes on
first start. See the runbook, *Portainer and other hosts without shell access*.

Behind **Nginx Proxy Manager, nginx or an F5** that terminates TLS: add `--proxy` (two hostnames,
an nginx config is generated). With certificates from your **PKI**: pass `--gateway-cert`,
`--gateway-key`, `--admin-cert`, `--admin-key` (and `--ca-file`). Details and the Nginx Proxy
Manager settings are in the runbook, *Reverse proxy and PKI*.

To update without downtime, run two instances of the gateway and admin API behind your proxy; see
[Updates without downtime](zero-downtime-updates.md). For Kubernetes instead of Compose, see [Kubernetes](kubernetes.md).

Production never uses the bundled Keycloak: point `UME_OIDC_AUTHORITY` at your existing Keycloak, AD FS or Entra ID.
Roles, department codes and AD groups are mapped by claims; see [Identity provider and roles](identity-and-roles.md).

`deploy/compose.prod.yaml` is the recommended, self-contained production file (hardened,
read-only, non-root, internal data network). The older route, `aspire publish -o deploy\generated`
plus `compose.hardening.yaml`, is what `.\deploy\Test-Deployment.ps1` verifies; it is kept in sync
with the new file.

The Aspire-generated alternative: generate Compose with
`aspire publish -o deploy\generated --non-interactive`.
**Never deploy the generated base alone**: use the mandatory hardening override and
operator-mounted secrets/certificates in [the runbook](runbook.md).
`.\deploy\Test-Deployment.ps1` builds and verifies an isolated production-shaped stack
with RAM-only test secrets and cleans up only its own resources.

Open `UmeLlmGateway.slnx` in Visual Studio and select `Ume.LlmGateway.AppHost` as
the startup project. Its `Properties\launchSettings.json` contains the HTTPS/HTTP
dashboard profiles; root `aspire.config.json` selects the same project for CLI commands.
The original development secret-store ID is preserved. When migrating an existing
checkout, set `DevelopmentVolumes:Postgres` (and `DevelopmentVolumes:Ollama`, if used)
in AppHost user secrets to the previous Docker volume names before starting, so existing
development data is reused. These optional overrides apply only in local run mode.
