# Kubernetes

`deploy/kubernetes` has Kustomize manifests for running the gateway on Kubernetes, with updates that do not interrupt
clients. They are the same services, settings, certificates and secrets as the [Docker Compose setup](production-setup.md),
so you can move between the two. Docker Compose stays the default and is unaffected.

## What you get

| Part | Contents |
|---|---|
| `base/` | Gateway and admin API (2 replicas each, rolling updates, PodDisruptionBudgets, probes on `/health/*`), Services, and the migrations Job |
| `components/datastores/` | Optional single-instance Postgres (StatefulSet, 20 GiB volume) and Redis, configured like Compose: TLS, separate database roles, Redis ACL |
| `components/dataapi/` | Optional [Data API](data-access.md) |
| `example/` | An overlay to copy: namespace, images, settings, Ingress |
| `create-secrets.sh` | Creates the Kubernetes secrets from what `init-deployment.sh` generates |
| `deploy.sh` | Deploys or updates an overlay: migration first, then a rolling update |

Every pod runs as non-root with a read-only file system, no privileges and the runtime's default seccomp profile, so the
namespace can enforce the **restricted** Pod Security Standard. Requirements: Kubernetes 1.27 or later, `kubectl`
1.27 or later (it includes Kustomize), and an Ingress controller or Gateway API implementation.

## Install

1. **Generate certificates and secrets** with the same script as for Compose. `--no-chown` leaves the files readable for
   you (the containers read them from Kubernetes secrets, not from disk):

   ```bash
   ./deploy/init-deployment.sh --no-chown --oidc-authority https://idp.example.se/realms/ume \
       --gateway-host llm.example.se --admin-host llm-admin.example.se
   ```

   It creates an internal CA, a certificate per service, database and Redis passwords, the key pepper, the Data Protection
   certificate and connection strings to the hosts `postgres` and `redis` (the Service names in `components/datastores`).
   It also writes `deploy/.env`, which Kubernetes does not use. **Back up `deploy/secrets/.pepper` and
   `deploy/certs/data-protection.pfx`** outside the cluster: without them a restored database is unusable.

2. **Create the secrets** in your namespace:

   ```bash
   ./deploy/kubernetes/create-secrets.sh --namespace ume-gateway --datastores
   ```

   Leave out `--datastores` if you bring your own Postgres and Redis (see below). The script can run again after
   `init-deployment.sh --renew-certs`; restart the deployments afterwards (`kubectl rollout restart`). Keep
   `deploy/certs/ca.key` offline between renewals; `create-secrets.sh` does not need it.

3. **Make an overlay.** Copy `deploy/kubernetes/example` (for example to `deploy/kubernetes/prod`, or into your GitOps
   repository with the `../base` paths adjusted) and change:
   - `images`: the release's images (`ghcr.io/thekim1/llmgateway/<service>`), pinned by digest from the release notes.
     A release bundle already pins the base.
   - `Oidc__Authority` and `Gateway__BaseUrl`; claim mapping as in [Identity provider and roles](identity-and-roles.md)
     (`Oidc__RoleClaim`, `Oidc__RoleGroups__gateway-admin`, ...).
   - The Ingress host names and TLS secrets. The pods speak HTTPS, so the controller must connect with TLS: the example
     annotations are for ingress-nginx; with Traefik use a ServersTransport, with the Gateway API an HTTPRoute and a
     BackendTLSPolicy. Pass the Host header through and turn off buffering, or streamed answers arrive all at once.
   - Optionally `../components/dataapi`, more replicas, resources, node placement.

4. **Deploy:**

   ```bash
   ./deploy/kubernetes/deploy.sh deploy/kubernetes/prod
   ```

5. **Register** `https://<admin host>/signin-oidc` and `/signout-callback-oidc` at your identity provider, sign in, and
   list the Ingress controller's pod network under *Settings > Trusted proxies*, so the gateway reads the client address
   from `X-Forwarded-For` (needed for [client address restrictions](client-addresses.md)).

Settings go into the overlay as ConfigMap literals named the way the apps read them (`Section__Key`), for example
`Gateway__Security__SourceAddress` or `OTEL_EXPORTER_OTLP_ENDPOINT`. Secrets stay in the secret files.

## Updates without downtime

Change the image digests in the overlay and run `deploy.sh` again. It:

1. applies configuration and the data stores, and waits for Postgres and Redis;
2. deletes the previous migrations Job (a Job cannot change), runs it with the migrator's credentials and waits until it
   succeeds. If it fails, it stops here, with the old version still running;
3. applies the rest. The gateway and admin API roll one pod at a time (`maxUnavailable: 0`, `maxSurge: 1`). A new pod
   receives traffic only once `/health/ready` passes. An old pod then fails readiness, leaves the Service endpoints during
   its 10-second drain while still answering (with `Connection: close`), and finishes its requests within 310 seconds
   (`terminationGracePeriodSeconds: 330`). How this works is described in [Updates without downtime](zero-downtime-updates.md).

Only releases the [upgrade guide](upgrade-guide.md) marks **Rolling update: yes** may be rolled this way; for the
others, scale the deployments to 0, run `deploy.sh`, and they come back with the new version.

**Argo CD or Flux** apply the overlay themselves. Run the migration first by marking the Job as a pre-sync hook in your
overlay, for example for Argo CD:

```yaml
patches:
  - target: {kind: Job, name: migrations}
    patch: |-
      - op: add
        path: /metadata/annotations
        value: {argocd.argoproj.io/hook: PreSync, argocd.argoproj.io/hook-delete-policy: BeforeHookCreation}
```

## Your own Postgres and Redis

`components/datastores` is meant for evaluation and small installations: one Postgres and one Redis, so updating them
is a short outage. In production prefer your platform's database service or an operator (CloudNativePG for Postgres).
Then:

- Create the database and roles as `deploy/postgres/init.sql` does: `ume_migrator` owns the database `gatewaydb`, and
  `ume_gateway`, `ume_admin` and (for the Data API) `ume_data` may only connect. The migrations Job grants the rest.
- Require TLS. The connection strings verify the server certificate against `ca.pem` (`SSL Mode=VerifyFull`): append
  your database's and Redis's CA to `deploy/certs/<service>/ca.pem` for gateway, adminapi, migrations and dataapi.
- Redis needs TLS and a user `gateway` with the commands and keys in `deploy/redis/users.acl.example`.
- Edit `deploy/secrets/<service>/ConnectionStrings__gatewaydb` and `ConnectionStrings__redis` for gateway, adminapi,
  migrations and dataapi, run `create-secrets.sh` without `--datastores`, and remove the component from the overlay.

## Scaling and placement

The gateway is stateless; add replicas for throughput (`patches` in the example). Pods spread across nodes where
possible, and the PodDisruptionBudgets keep one of each running through node drains. Each Data API replica applies
its per-client rate limit on its own, so two replicas allow a client twice the requests. Requests and limits in the base
are starting points: the gateway's memory grows with concurrent streams and large attachments (see
[performance](performance.md)).

## Tested

`deploy/tests/kubernetes-e2e.sh` runs in CI on a kind cluster: secrets from `init-deployment.sh`, `deploy.sh` with the
data stores and the Data API under the restricted Pod Security Standard, then a rolling restart of the gateway and admin
API while a pod in the cluster sends requests to their Services. It fails on a single lost request; see
[Updates without downtime](zero-downtime-updates.md#tested) for the numbers.
