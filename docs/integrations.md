# Integrations: platforms that use gateway keys for others

How another system (a RAG platform, an agent platform, a chat front end) connects to the gateway as one object,
runs each of its chatbots on a gateway key of the department that owns it, and fills its own settings pages with
the gateway's values, without the Data API.

> **Status.** Design, 2026-11-11. **Nothing here is implemented.** Decided by the gateway owner: a chatbot
> (assistant) is configured with a gateway key of its own, set up in the gateway; gateway keys are reused on the
> Integration API; settings are entered once; streamed cost is wanted. It replaces the delegation design kept in the
> RAG platform's repository (`plans/gateway-changes/design.md` §5, draft ADR 020). The gateway still never depends
> on a particular platform: every part is a generic feature.

## Goals

- **Cost follows the department.** A platform runs many chatbots: public ones without sign-in, internal ones behind
  sign-in. Each is charged to the department and team that owns it, and can be given its own budget and limits.
- **The key is the unit.** A chatbot's models, residency, PII policy, limits and budget are an ordinary gateway
  key's settings. No new cost object, and every existing report, budget and alert works per chatbot as it is.
- **Enter things once.** What the gateway owns is set in the gateway and shown in the platform as choices and
  read-only facts; nobody types a model name, a price, a team or a secret in two places.
- **No Data API for integrations**, no new service, and nothing on the request path beyond header and in-memory
  lookups: no Postgres call and no extra Redis round trip ([performance.md](performance.md)).

## Today (v0.9.2)

| Surface | Credential | A RAG platform uses it for |
|---|---|---|
| Gateway `/v1` | `ume-sk-` keys of one team, one per residency level and service | every model call; all of it is one line under that team |
| Data API `/v1/catalog/*` | a separate `ume-dk-` key, `catalog.read` | departments and teams, providers, models and prices |

The platform also keeps by hand what the gateway does not hand out: a model's output limit and tool support.

## Overview

```
 gateway admin UI                          platform admin UI
  department admin creates key             editor picks the key for the chatbot
  "Stadsbyggnad chatbot", used             (list comes from the gateway), then a
  through: Ume.Rag; sets budget            model from that key's models
        │                                          │
        ▼                                          ▼
 ┌─ Gateway /v1 (data plane) ───────────────────────────────────────────────┐
 │ platform service authenticates with its own key (the credential)         │
 │ and sends  x-ume-on-behalf-of: <id of the chatbot's key>                 │
 │ → the request runs as that key: its limits, policies, budgets, usage     │
 │ GET /v1/models, GET /v1/ume/context: what to show in the platform        │
 └──────────────────────────────────────────────────────────────────────────┘
 ┌─ Admin API /integration/v1 (control plane) ──────────────────────────────┐
 │ usage and budget status of the integration's keys                        │
 └──────────────────────────────────────────────────────────────────────────┘
 Data API: unchanged (BI, SIEM). Security events: unchanged (OpenTelemetry).
```

The platform holds one secret per service (its credential keys). A chatbot's key is chosen by id; its secret is
never shown, copied or stored anywhere, so there is nothing to paste, leak or rotate per chatbot.

## Integration and its two kinds of key

A row in `Integrations` (name, description, owner team, active), created by gateway admins under
*Access → Integrations*. `VirtualKey` gains `IntegrationId` and `IntegrationRole`:

| Role | Created by | What it is |
|---|---|---|
| `Credential` | gateway admin, in the integration's owner team | What a platform service authenticates with. Has a secret, an address list, an expiry, rotation. Can also make the platform's own calls (model check, sync), charged to the owner team |
| `Assigned` | the key's ordinary managers (department and team admins), in their own team: *Used through: Ume.Rag* on the key form | A chatbot's key. **Cannot authenticate by itself** (refused with `key_integration_only`); it is only used by reference through that integration. No secret is shown; rotation is not offered |
| – (none) | as today | An ordinary key |

A department admin thus sets up a chatbot's key alone: name, models, residencies, PII policy, content guard, rate
limits, and a key budget. It then appears in the platform for that department's editors. Disabling or revoking the
key stops the chatbot at once; that is the department's own decision and the platform shows why.

## Acting as a key

`x-ume-on-behalf-of: <key id>` on a request authenticated by a credential key.

| Case | Answer |
|---|---|
| No header | the credential key itself, as any key |
| Header malformed or repeated | `400 invalid_request` |
| Caller is not a credential key, or the id is not an assigned key of the same integration | `403 delegation_not_allowed` (unknown ids answer the same) |
| Assigned key disabled, revoked, expired, or its team or department inactive | `403` with the key's ordinary reason (`key_disabled`, ...) |
| Usable | the request continues **as the assigned key**; the answer echoes `x-ume-on-behalf-of` |

From there on the pipeline sees the assigned key and needs no other change:

| Aspect | From |
|---|---|
| Client address list, residency ceiling header (`x-ume-max-residency`) | the credential key and the request |
| Models, providers, residencies, PII, attachments, provider tools and storage, content guard | the assigned key |
| Rate, token and concurrency limits | the assigned key (a limit per chatbot) |
| Request policy, routing rules | the assigned key's, its team's and department's |
| Budgets and token quotas | the assigned key, its team, its department |
| Usage record, security events, `LastUsedAt` | the assigned key; the credential key's id is logged on security events |

The credential key's own model and residency lists do not apply to a request made as another key: the grant is the
assignment, made by the people who manage the assigned key. The ceiling header still narrows every request.

**In the code.** After authentication, one stage swaps `state.Key` for the assigned key and keeps the caller in
`state.CallerKey`. Assigned keys are loaded with their integration into the key cache (one query on a cold load,
`Team` and `Department` included, allow-lists indexed) and held in a frozen dictionary by id; every key change
already publishes `InvalidationKind.Keys`. Per request: one header lookup and one dictionary lookup.

Refusals are accounted against the credential key and logged as security event 1006
`gateway.delegation.refused` (`Reason`, `OnBehalfOf`, `KeyId`, `KeyPrefix`, `TeamId`, `DepartmentId`).

### Run metadata

`x-ume-run-id` and `x-ume-step` (any key; opaque ids, at most 100 characters kept, never refused) are stored on the
usage record, so a platform can split a chatbot's cost by step (answer, condense, rerank, embedding) and join its
own records to the gateway's. Ids only: no names, content or user ids (ADR 003).

### Cost on streamed answers

Today `x-ume-cost-usd` is a response header, so only complete answers carry it. With `x-ume-stream-cost: 1` the
gateway writes one Server-Sent Events comment after the provider's last event:

```
: ume-cost-usd=0.00012345
```

SSE clients ignore comment lines, and it comes after the provider's own end of stream; it is still opt-in. The cost
is the one the usage record gets (computed once). An aborted stream has no such line; the usage record is the
record. Cost: one small write per stream.

## Settings: who owns what

The rule: a setting has one owner. The other system reads it and shows it; it never keeps an editable copy.

| Setting | Owner | In the platform |
|---|---|---|
| Departments, teams, cost centres | gateway | read; scopes what an editor sees |
| Department member groups (sign-in scope from IdP groups) | gateway | read and applied with the same rule |
| Sign-in, roles from groups | the shared IdP realm | – |
| Models: name, kind, residencies, context window, **output limit**, tool support, prices | gateway | model picker and limits filled in; the manual fields on the platform's model list go away |
| Which key a chatbot or knowledge base runs on | **platform** (a key id) | picker of the assigned keys of the object's department |
| The key's models, residencies, PII policy, content guard, attachment and tool policy, rate limits | gateway (the key) | shown read-only on the chatbot; the model picker lists only that key's models within the chatbot's residency level |
| Budgets and alerts | gateway | status shown read-only (limit, spent, period) with a link to the gateway |
| Output cap, parameter overrides on the key (request policy) | gateway, as a guard rail | the effective cap is shown; the chatbot's own answer length cannot exceed it |
| System prompt, temperature, answer length, retrieval settings | platform (the chatbot) | – . A key used for a chatbot should have no system prompt; the platform warns when it has |
| Who may call a chatbot (apps, public or signed-in), per-caller limits | platform | – |
| Residency level of the content | platform (classification) | sent as the ceiling on every call; saving checks the key can serve it |
| Usage and cost | gateway is the record | per chatbot read from the gateway; the platform's own finer split by step joins on the run id |

To make this possible the gateway adds `MaxOutputTokens` to a model (today only `ContextWindow` and `Features`
exist there) and hands all of it out in discovery.

## Discovery (data plane)

Answered from the in-memory catalogue and key cache, with an `ETag`, so a platform polls with `If-None-Match` and
usually gets 304. Neither is accounted as a model request.

**`GET /v1/models`** gains, per model, under `ume`: `kind`, `residencies` (exists), `context_window`,
`max_output_tokens`, `features`, and `pricing` (`input`, `cached_input`, `output` per million tokens, USD). With
`x-ume-on-behalf-of` it lists the assigned key's models, which is the chatbot's model picker and the check on save.

**`GET /v1/ume/context`** for a credential key:

```json
{
  "gateway": { "version": "0.10.0", "adminUrl": "https://…", "features": ["max-residency", "act-as-key", "run-metadata", "stream-cost"] },
  "integration": { "id": "…", "name": "Ume.Rag" },
  "key": { "id": "…", "prefix": "ume-sk-AbC1", "scopes": ["usage.read"] },
  "departments": [{ "id": "…", "name": "…", "costCentre": "3000", "isActive": true, "memberGroups": ["…"] }],
  "teams": [{ "id": "…", "departmentId": "…", "name": "…", "code": "…", "isActive": true }],
  "keys": [{
    "id": "…", "name": "Stadsbyggnad chatbot", "teamId": "…", "status": "Active", "expiresAt": null,
    "allowedModels": [], "allowedResidencies": ["OnPrem", "Eu"], "piiPolicy": "Redact", "contentGuard": "Off",
    "attachmentPolicy": "Allowed", "allowProviderTools": false, "requestsPerMinute": 120, "tokensPerMinute": null,
    "maxOutputTokens": 2000, "hasSystemPrompt": false
  }]
}
```

`departments` and `teams` are those with at least one assigned key, plus the owner team. An ordinary key gets its
own key, team and department only. `features` lets a client test for a capability instead of comparing versions;
`adminUrl` lets the platform link to the key or budget in the gateway.

## Integration API (control plane)

`/integration/v1` on the Admin API, for what queries the database, which the data plane must not do (ADR 001).

- **Authentication:** a credential key (`Authorization: Bearer ume-sk-…`) carrying the scope. The Admin API already
  holds the hasher and pepper. Status, expiry, grace period and address list apply as on the gateway; lookups are
  cached 30 s and dropped on a `Keys` invalidation. No cookie, no CSRF, its own rate limit.
- **Scopes are per key**, set by gateway admins, empty by default: a platform gives `usage.read` to its admin
  service only, so a key that serves public chat can read nothing.

| Scope | Endpoint | |
|---|---|---|
| `usage.read` | `GET /integration/v1/usage?from=&to=&groupBy=day,key,model,step&keyId=&runId=` | Requests, tokens and cost in USD of **this integration's assigned and credential keys only**, from usage records and monthly totals |
| `usage.read` | `GET /integration/v1/budgets?keyId=` | Limit, spent and period of the budgets that apply to a key (its own, its team's, its department's) |

Not offered: creating or changing keys, setting budgets, other keys' usage. Keys and budgets are made in the gateway
by the people responsible for the money.

## Reports and the Data API

- Nothing new is needed to follow cost: an assigned key is a key, so usage per key, team and department, budgets,
  alerts and the CSV export already show each chatbot. The key list gets an *integration* column and filter; the
  integration page lists its keys and their use per department.
- Data API: `usage/records` gains `runId` and `step` (appended); `catalog/keys` gains `integrationId` and
  `integrationRole`; `catalog/integrations` and a model's `maxOutputTokens` are added; `security/requests` includes
  the new refusals.
- `DataApi:MinimumGroupSize` counts distinct keys per department. Each chatbot is a key, so a department with five
  chatbots is no longer folded as it would be behind one shared platform key.

## Data model

| Migration | Change |
|---|---|
| `Integrations` (phase 1) | table `Integrations`; `VirtualKeys`: `IntegrationId uuid NULL`, `IntegrationRole smallint NOT NULL DEFAULT 0`, `IntegrationScopes text[] NOT NULL DEFAULT '{}'`; `ModelDeployments.MaxOutputTokens integer NULL` |
| `UsageRunMetadata` (phase 2) | `UsageRecords`: `RunId varchar(100) NULL`, `Step varchar(100) NULL` (nullable columns: no table rewrite; not kept in monthly totals) |

`app-roles.sql`: `ume_data` reads `Integrations` and the new key columns. No new budget scope, no change to the
retention function, no new Redis keys.

## Request path cost

| Addition | Per request |
|---|---|
| `x-ume-on-behalf-of` | one header lookup; one frozen-dictionary lookup when present |
| Rate limit and budget | unchanged: the same counters as any key's request, in the same single round trip |
| Usage record | two nullable columns more in the batched COPY, off the request path |
| Stream cost | opt-in: one write after the last event |
| Discovery | not on the request path; 304 on an unchanged catalogue |

Verified with the `*Pipeline*` benchmarks before and after (with and without the header) and an allocation test of
the new stage.

## What a platform changes (Ume.Rag as the example)

| Today | With this |
|---|---|
| Organisation and model sync read the Data API with a Data API key | read `/v1/ume/context` and `/v1/models` with a credential key; the Data API key and network path go away |
| Per-level keys of the pilot team in each service | one credential key per service; the ceiling header stays on every call |
| All traffic charged to the pilot team | an assistant, and a knowledge base for its indexing, has a gateway key chosen from the department's assigned keys; every call sends `x-ume-on-behalf-of` and checks the echo |
| Output limit and tool support typed in per model | read from the gateway |
| Own cost estimate from mirrored prices | the gateway's cost: `x-ume-cost-usd`, the stream comment, or `usage.read` |
| Budget per assistant: not possible | the key's budget in the gateway; 402 names the scope and the platform shows the assistant as unavailable |

The platform's own keys for its callers (which app may use which chatbot) stay in the platform. A public user's
browser never holds a gateway key.

## Other platforms later

A new platform is another integration and uses the same interfaces: the data plane with `x-ume-on-behalf-of`,
discovery, the Integration API, security events over OpenTelemetry, and the Data API for reporting. No code is
loaded into the gateway process. Candidates for later, off the request path: webhooks from the Admin API (budget
threshold, key disabled, catalogue changed) so platforms need not poll.

The contract (`/v1/ume/context`, the `ume` model fields, the headers and `/integration/v1`) is published as an
OpenAPI document in this repository and changed by addition only within `v1`, so a platform's fake gateway can be
tested against it.

## Phases

Each is additive and safe for a rolling update ([zero-downtime-updates.md](zero-downtime-updates.md)).

| Phase | Content | Done when |
|---|---|---|
| 1 Consolidate and share | `Integrations`, key roles and scopes, admin UI, `MaxOutputTokens`, `/v1/models` facts, `/v1/ume/context`, ETag | a platform runs with gateway keys only, fills its pickers from the gateway, and the Data API can be switched off |
| 2 Keys per chatbot | `x-ume-on-behalf-of`, echo, event 1006, run metadata, stream cost | two departments' chatbots show as their own keys in the usage report, and a key budget stops one of them with 402 |
| 3 Read back | Integration API (`usage.read`) | the platform shows the gateway's cost and budget status per chatbot |
| 4 Later | webhooks | – |

## ADR drafts

| ADR | Decision | Rationale / consequences |
|---|---|---|
| 020 | A platform is an integration; its credential keys can act as keys assigned to it, and an assigned key cannot be used directly | Supersedes the draft "a key can act for listed teams". Departments must pay for and limit their own chatbots on a shared platform. A key already carries everything that is wanted per chatbot (models, residency, PII, limits, budget, usage), so the chatbot's unit in the gateway is a key, made by the department's own admins and marked as used through the integration. The platform authenticates with a credential key and names the assigned key by id in `x-ume-on-behalf-of`; the request then runs as that key, so budgets, rules, policies, usage and reports need no new concept. An assigned key has no usable secret: nothing is copied into the platform, leaked or rotated per chatbot. Resolution is one in-memory lookup: no Postgres and no extra Redis round trip. The credential key's own allow-lists do not apply to acted requests; the grant is the assignment. |
| 021 | Integrations read configuration from the data plane and reports through a scoped Integration API on the control plane, with gateway keys; each setting has one owner | The Data API is optional and built for reporting, so requiring it for a platform's configuration was a wrong dependency. What a key may use (models with prices and limits, teams, assigned keys and their settings, features) is answered by the gateway from memory, so a platform shows the gateway's values instead of keeping copies. What queries the database (usage, budget status) is `/integration/v1` on the Admin API, keeping ADR 001; it accepts credential keys with per-key scopes, empty by default, so rotation, expiry, address lists and audit are reused and no second credential store exists. |

## Open points

- A platform-wide cap: acted requests count against the assigned key's limits only. If one is needed (so indexing
  cannot starve chat), the credential key's concurrency limit can be counted too, in the same script.
- Whether `pricing` on `/v1/models` should list every eligible target instead of the first (a fallback can cost
  differently; the actual cost is on the answer either way).
- `x-ume-stream-cost` with the Anthropic and Responses stream shapes: verify with the SDKs first.
