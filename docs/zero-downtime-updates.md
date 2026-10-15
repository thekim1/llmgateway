# Updates without downtime

By default the gateway runs as one instance of each service, and an update stops it, starts the new version and runs the
database migration in between: a minute or two in which clients get errors. That is fine for many installations. If
clients must keep working through an update, run two instances behind a load balancer and replace them one at a time.
It is opt-in: nothing changes for an installation that does not ask for it.

| You deploy with | Single instance (default) | Without downtime |
|---|---|---|
| Docker Compose (`compose.prod.yaml`) | `docker compose ... up -d --wait` | Add `compose.rolling.yaml` and update with `rolling-update.sh` (below) |
| Portainer (`compose.portainer.yaml`) | Update the stack | Not supported: Portainer recreates every container at once |
| Kubernetes | – | Built in: see [Kubernetes](kubernetes.md) |

## How it works

All state lives in Postgres and Redis: rate limits, the spend ledger, circuit breakers, live-session tracking, cache
invalidation and the key ring that protects admin sign-ins. Any instance can therefore serve any request, and a user
signed in to the admin UI stays signed in when the next request reaches another instance. Each instance writes its own
queued usage records before it exits, and only one admin API runs the nightly data cleanup at a time.

When an instance is told to stop (`docker stop`, a Kubernetes pod being replaced), it:

1. **Stops being ready.** `/health/ready` answers 503 at once, so a load balancer that polls it (Kubernetes, F5,
   HAProxy) stops sending new requests. `/health/live` stays 200, so nothing restarts the instance.
2. **Keeps answering for the drain time** (`Shutdown__DrainSeconds`). Each response then says `Connection: close`, so a
   proxy closes its pooled connection after the current request. Without this, a proxy can send a request on a pooled
   connection at the moment the instance closes it. The proxy retries such a request when it is a GET, but not when it is a
   POST (every model call), so the client gets a 502.
3. **Closes its listener.** New connections are refused, and the proxy sends the request to the other instance. The few
   connections the operating system had already accepted for it but the instance never read are reset; the request in
   them did not run, so the proxy can retry them too (see the nginx settings below).
4. **Finishes the requests it has** within the shutdown timeout (`DOTNET_SHUTDOWNTIMEOUTSECONDS`), including long
   streamed answers, then exits.

The database is migrated before the first new instance starts. Until the last old instance has stopped, the old version
runs against the new schema. That only works when the release's migration is backward compatible (adds, does not remove
or rename). The [upgrade guide](upgrade-guide.md) marks every release **Rolling update: yes** or **no**. For a "no"
release, stop everything, migrate and start the new version, as for a single instance.

## Docker Compose

The overlay `deploy/compose.rolling.yaml` adds a second gateway (`gateway-b`) and a second admin API (`adminapi-b`) with
the same image and settings, on their own host ports (8444 and 9444 by default). A reverse proxy in front lists both
ports of each service. Clients that connect to the gateway directly, without a proxy, cannot use this: something has to
choose the instance.

### Set it up

New installation:

```bash
./deploy/init-deployment.sh --proxy --rolling      # writes proxy/nginx.conf with both slots
docker compose -f deploy/compose.prod.yaml -f deploy/compose.rolling.yaml --env-file deploy/.env up -d --wait
```

Existing installation: add the two ports to `deploy/.env` (or re-run `init-deployment.sh --proxy --rolling`; it keeps your
secrets and certificates, and saves the old `.env` as `.env.bak`):

```bash
UME_GATEWAY_B_PORT=8444
UME_ADMIN_B_PORT=9444
```

Then start with both files as above, and add the second slots to the proxy.

**nginx** (`deploy/proxy/nginx.conf.example`, filled in by `init-deployment.sh --proxy --rolling`):

```nginx
upstream ume_gateway {
    server docker-host:8443;
    server docker-host:8444;
    keepalive 32;
}
upstream ume_admin {
    server docker-host:9443;
    server docker-host:9444;
}
```

And in each `location`:

```nginx
proxy_next_upstream error timeout non_idempotent;
proxy_next_upstream_timeout 10s;
proxy_next_upstream_tries 2;
proxy_connect_timeout 5s;
```

`proxy_next_upstream` makes a failed connection go to the other slot. `non_idempotent` lets nginx retry a POST (every
model call) too. Without it, a POST in a connection the stopping slot reset is lost as a 502. The CI test lost 3 of
62 000 that way. A retry happens only when the connection failed before any response, and `proxy_next_upstream_timeout`
allows it only within 10 seconds, so a request that ran long and then failed is never repeated. The one case where a
model call could run twice is a gateway that crashes within 10 seconds of receiving it, after passing it on to the
provider; that client would otherwise get an error.

**Nginx Proxy Manager:** a proxy host has one upstream. Put the `upstream` blocks above in a custom configuration
(`/data/nginx/custom/http_top.conf`) and point the proxy host's *Advanced* tab at them (`proxy_pass https://ume_gateway;`).

**F5, HAProxy or another load balancer with health monitors:** add both ports to the pool and monitor
`GET /health/ready` over HTTPS (expect 200). Let it resend a request when the connection fails before a response (F5:
*Reselect Tries*; HAProxy: `retry-on conn-failure empty-response` with `option redispatch`). Set the drain time to more than the monitor interval times the number of
failures before a member is marked down, for example `UME_DRAIN_SECONDS=15` with a 5-second interval and 2 failures.

### Update

Change the image references in `deploy/.env` (or unpack the new release bundle over `deploy/`), then:

```bash
./deploy/rolling-update.sh            # --pull to pull the images first
```

It checks that all four slots are healthy, runs the migration, then replaces `gateway`, `gateway-b`, `adminapi` and
`adminapi-b` one after the other. It waits until each new slot is healthy, plus `UME_ROLLING_SETTLE_SECONDS` (15) so the
proxy takes it back into rotation, before stopping the next. It refuses to stop a slot while its partner is unhealthy. A
slot whose image did not change is left alone; `--force` recreates it anyway, for example after `--renew-certs`. The
optional Data API has a single slot and is briefly unavailable while it restarts.

### Settings

In `deploy/.env`, read by `compose.rolling.yaml`:

| Variable | Default | Meaning |
|---|---|---|
| `UME_GATEWAY_B_PORT`, `UME_ADMIN_B_PORT` | 8444, 9444 | Host ports of the second slots (bound to `UME_GATEWAY_BIND` / `UME_ADMIN_BIND`) |
| `UME_DRAIN_SECONDS` | 5 | Seconds a stopping slot keeps answering with `Connection: close` (`Shutdown__DrainSeconds`) |
| `UME_SHUTDOWN_TIMEOUT_SECONDS` | 300 | Longest time a stopping gateway finishes its requests, drain included |
| `UME_ROLLING_SETTLE_SECONDS` | 15 | Pause after each slot is healthy (environment variable for `rolling-update.sh`) |

`Shutdown__DrainSeconds` (0–300, default 0) is a setting of every service, so it also works outside these files.

## What is still interrupted

- **Live audio sessions** (`/v1/realtime`, WebSocket) on a stopping instance end when the stop signal arrives; clients
  reconnect and land on another instance. Ordinary and streamed requests finish.
- **Requests longer than the shutdown timeout** (300 s for the gateway) are cut off. Raise
  `UME_SHUTDOWN_TIMEOUT_SECONDS` (and the Compose `stop_grace_period`) if you have longer ones.
- **Postgres and Redis** run as single instances in the Compose stack. Updating their images is a short outage; plan it
  separately (they change rarely). A highly available database means a managed service or an operator, see
  [Kubernetes](kubernetes.md).
- **Releases marked "Rolling update: no"** need a planned stop.
- **The first update to this version** still stops instances of the previous version, which have no drain. A few POSTs
  can fail at the moment each old slot stops; later updates do not have this.

## Tested

`deploy/tests/compose-rolling-e2e.sh` and `deploy/tests/kubernetes-e2e.sh` run in CI (workflow *Deploy end to end*) on
every change to `deploy/` or the shutdown code. They replace every instance while clients send POST and GET requests to
the gateway and load the admin UI, and fail on a single lost request. A local run on 16 October 2026 gave:

| Run | Requests during the update | Lost |
|---|---|---|
| Compose, nginx in front, all four slots replaced | 128 780 POST, 327 140 GET, 350 417 admin UI | 0 |
| Same, with `UME_DRAIN_SECONDS=0` | 96 635 POST, 289 596 admin UI | 1 POST (502) |
| Kubernetes (kind), rolling restart of gateway and admin API | 40 195 POST, 115 072 GET, 123 282 admin UI | 0 |
