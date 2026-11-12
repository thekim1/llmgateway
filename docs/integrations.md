# Integrations: platforms that use gateway keys for others

How another system (a RAG platform, an agent platform, a chat front end) connects to the gateway as one object,
runs each of its chatbots on a gateway key of the department that owns it, and fills its own settings pages with
the gateway's values, without the Data API.

> **Status.** Design accepted 2026-11-11. **Phases 1 to 3 are implemented**: integrations, credential and assigned
> keys, `/v1/ume/context`, the model facts on `/v1/models`, the model output limit (phase 1); acting as an assigned key,
> run metadata, the cost of streamed answers and the Data API fields (phase 2); the Integration API with usage and
> budget status (phase 3). After the RAG platform's review: the credential key's residencies bound every key it acts
> as, the scope `organisation.read`, the contract as an OpenAPI document, and department member groups. Webhooks (phase 4) are not planned yet. Decided by the gateway owner: a chatbot
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

## Before 0.10.0

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
| `Assigned` | the key's ordinary managers (department and team admins), in their own team: *Used through: Ume.Rag* on the key form | A chatbot's key. **Cannot authenticate by itself** (refused with `403 integration_only`); it is only used by reference through that integration. No secret is kept or shown; rotation is refused (409) |
| – (none) | as today | An ordinary key |

A department admin thus sets up a chatbot's key alone: name, models, residencies, PII policy, content guard, rate
limits, and a key budget. It then appears in the platform for that department's editors. Disabling or revoking the
key stops the chatbot at once; that is the department's own decision and the platform shows why.

As built: the integration and role are set when a key is created and never change (an update that names another is
400). A credential key's settings, scopes included, are changed by gateway admins only (403 for others); its managers
may rotate and revoke it, and rotation keeps the integration, role and scopes. Deactivating an integration refuses
all its keys with `key_disabled` (security event reason `integration_inactive`); an integration is deleted only when
all its keys are revoked, and a team that owns one cannot be deleted. The admin UI has *Access → Integrations*
(gateway admins), an *Integration* section on the key form and detail, and the key list shows the integration.

## Acting as a key

`x-ume-on-behalf-of: <key id>` on a request authenticated by a credential key.

| Case | Answer |
|---|---|
| No header | the credential key itself, as any key |
| Header not one key id (GUID), or repeated | `400 invalid_request` |
| Caller is not a credential key, or the id is not an assigned key of the same integration | `403 delegation_not_allowed` (unknown ids answer the same) |
| Assigned key disabled, revoked, expired, or its team or department inactive | `403` with the key's ordinary reason (`key_disabled`, ...) |
| Usable | the request continues **as the assigned key**; the answer echoes `x-ume-on-behalf-of` |

From there on the pipeline sees the assigned key and needs no other change:

| Aspect | From |
|---|---|
| Client address list, residency ceiling header (`x-ume-max-residency`) | the credential key and the request |
| Residencies | those **both** keys allow: the assigned key's, within the credential key's |
| Models, providers, PII, attachments, provider tools and storage, content guard | the assigned key |
| Rate, token and concurrency limits | the assigned key (a limit per chatbot) |
| Request policy, routing rules | the assigned key's, its team's and department's |
| Budgets and token quotas | the assigned key, its team, its department |
| Usage record, security events | the assigned key; the usage record's `ViaKeyId` is the credential key |
| `LastUsedAt` | both keys |
| `x-ume-max-residency` required | when either key has `RequireMaxResidency` |

The credential key's own model list does not apply to a request made as another key: the grant is the assignment,
made by the people who manage the assigned key. Its **residencies are an upper bound**: a request made as an assigned
key reaches only residencies that both keys allow (a key without a list allows all). A platform can so give a service
that must never leave the building a credential key with `OnPrem` only, whatever the departments' keys allow, and a
forgotten header or a stolen credential cannot reach further. When the two keys have no residency in common the
request is refused (`403 delegation_not_allowed`, reason `no_common_residency`) and the context lists the key with
`usable: false`. The ceiling header still narrows every request.

The bound costs nothing per request: an assigned key is held in the cache once per credential key, and its residency
list is narrowed there when it is loaded (`VirtualKey.BoundBy`), so routing, `/v1/models` and the ceiling read it as
any key's.

**In the code.** `RequestStages.AuthenticateAsync` returns an `AuthenticatedKey` (the key the request is made as,
and `Via`, the credential key); the HTTP, live-session, model-list and context entry points all use it, so every
endpoint honours the header. Assigned keys are loaded with their integration into the key cache (one query on a cold
load, `Team` and `Department` included, allow-lists indexed) and held in a frozen dictionary by id
(`VirtualKey.AssignedKey`); every key change already publishes `InvalidationKind.Keys`. Per request: one header
lookup, and one dictionary lookup when the header is there.

Refusals are answered before the body is read, like authentication failures, and are not usage records (the
request never ran as either key). They are logged as security event 1006 `gateway.delegation.refused` (`Reason`:
`not_credential`, `key_not_assigned`, `key_revoked`, `key_expired`, `key_disabled`, `owner_inactive`,
`no_common_residency`; `RequestId`,
`OnBehalfOf` as sent, at most 100 characters, `Endpoint`, and the credential key's `KeyId`, `KeyPrefix`, `TeamId`,
`DepartmentId`). A malformed header is a client bug and logs nothing. `/v1/models` and `/v1/ume/context` with the
header answer for the assigned key, which is the chatbot's model picker and the check on save.

### Run metadata

`x-ume-run-id` and `x-ume-step` (any key; opaque ids, trimmed, at most 100 characters kept, control characters
replaced, never refused) are stored on the usage record (`RunId`, `Step`), so a platform can split a chatbot's cost by step (answer, condense, rerank, embedding) and join its
own records to the gateway's. Ids only: no names, content or user ids (ADR 003).

### Cost on streamed answers

`x-ume-cost-usd` is a response header, so only complete answers carry it. With `x-ume-stream-cost: 1` the gateway
writes one Server-Sent Events comment at the end of a streamed answer: before the closing `data: [DONE]` of an
OpenAI-style stream (clients stop reading there), after the last event of a stream without one (Anthropic Messages,
Responses):

```
: ume-cost-usd=0.00012345
```

SSE clients ignore comment lines, so other clients are unaffected; it is still opt-in. The cost (six decimals, USD)
is the one the usage record gets: it is calculated once, from the provider's token counts when the stream reports
them (ask for them with `stream_options.include_usage`), else from the estimate, as for any streamed answer. An
aborted or broken stream has no such line; the usage record is the record. Cost: one small write per stream.

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

**`GET /v1/models`** (phase 1) gains, per model, under `ume`: `kind`, `residencies` (alias) or `residency` (model; its
own when it has one, else its provider's), `context_window`, `max_output_tokens`, `features`, and `pricing`
(`input_per_million_usd`, `cached_input_per_million_usd`, `output_per_million_usd`, or null). For an alias the limits
are the smallest of the targets the key may use (null when one does not know it), the features those all have, and
the price the first target's. With `x-ume-on-behalf-of` (phase 2) it lists the assigned key's models, which is the
chatbot's model picker and the check on save.

**`GET /v1/ume/context`** (phase 1) for a credential key (field names in snake_case, like `/v1/models`):

```json
{
  "object": "ume.context",
  "gateway": { "version": "0.10.0", "admin_url": "https://…", "features": ["max-residency", "model-facts", "integrations"] },
  "key": { "id": "…", "name": "Ume.Rag api", "prefix": "ume-sk-AbC1", "team_id": "…", "integration_role": "Credential",
           "integration_scopes": ["usage.read"], "require_max_residency": true, "allowed_residencies": [] },
  "integration": { "id": "…", "name": "Ume.Rag", "owner_team_id": "…" },
  "departments": [{ "id": "…", "name": "…", "cost_center_code": "3000", "is_active": true }],
  "teams": [{ "id": "…", "department_id": "…", "name": "…", "code": "…", "is_active": true }],
  "assigned_keys": [{
    "id": "…", "name": "Stadsbyggnad chatbot", "description": null, "team_id": "…", "department_id": "…",
    "status": "Active", "owner_active": true, "expires_at": null,
    "allowed_models": [], "allowed_residencies": ["OnPrem", "Eu"], "usable": true, "allowed_providers": [], "pii_policy": "Redact",
    "content_guard_policy": "Off", "attachment_policy": "Allowed", "allow_provider_tools": false, "allow_provider_storage": false,
    "requests_per_minute": 120, "tokens_per_minute": null, "max_concurrent_requests": null,
    "max_output_tokens": 2000, "has_system_prompt": false
  }]
}
```

- `assigned_keys` are the integration's assigned keys that are not revoked (disabled and expired ones are listed with
  their `status`, so a platform can say why a chatbot stopped); never a secret or hash. `max_output_tokens` and
  `has_system_prompt` come from the key's and its team's request policy combined, as a request gets them.
  `allowed_residencies` is the key's own setting; a request made as the key reaches those the presented credential
  key (`key.allowed_residencies`, empty = all) allows too, and `usable` is false when that leaves none.
- `departments` and `teams` are those of the assigned keys, plus the credential key's own (the owner team). A
  credential key with the scope **`organisation.read`** gets **every department and team**, inactive ones included
  (`is_active`), so a platform can share an object with the whole organisation, or let an owner team be chosen before
  its department has made a key. The scope is per key: give it to the service that syncs the organisation, not to one
  that serves public traffic. An ordinary key gets `integration: null`, no assigned keys, and its own team and
  department.
- `features` lets a client test for a capability instead of comparing versions (phase 2 adds `act-as-key`,
  `run-metadata`, `stream-cost`; then `credential-residencies` for the residency bound, `organisation` for the
  scope and `member-groups` for the departments' member groups). `admin_url` is `Gateway:AdminUrl` (optional), for links to a key or budget.
- With the same scope each department carries **`member_groups`** (feature `member-groups`): the values of the
  identity provider's group claim whose members belong to the department, set on *Organisation* in the gateway
  ([Department scope](identity-and-roles.md#department-scope)). A platform gives a signed-in user the department's
  scope when the user's group claim holds one of them, compared ignoring case, as the gateway's own admin sign-in
  does. Membership never gives a role. Without the scope the field is absent.
- The answer comes from the key cache: a credential key is loaded with its integration's assigned keys (one more query
  on a cold load, and one for the organisation with the scope; none per request), and every key, team, department or
  integration change drops the cache, creating or deleting a department or team included. Like
  `/v1/models` it is not accounted and has no rate limit of its own; send `If-None-Match` to get `304`.

## Integration API (control plane)

`/integration/v1` on the Admin API, for what queries the database, which the data plane must not do (ADR 001).

- **Authentication:** a credential key (`Authorization: Bearer ume-sk-…`) of an active integration, carrying the scope
  (401 for any other key or none, 403 without the scope). The Admin API already holds the hasher and pepper. Status,
  expiry, grace period, owner team and department and the key's address list apply as on the gateway (the client
  address behind trusted proxies, as in the admin API's audit); lookups are cached 30 s per admin API instance and
  dropped on a `Keys` invalidation. No cookie and no CSRF (the endpoints only read); a signed-in session does not reach
  them. Its own rate limit per key, `Admin:IntegrationRequestsPerMinute` (60). Every read is logged as security event
  2006 `admin.data.read` with the actor `integration:<name>`.
- **Scopes are per key**, set by gateway admins, empty by default: a platform gives `usage.read` to its admin
  service only, so a key that serves public chat can read nothing. (`organisation.read` is a scope too, for
  [`/v1/ume/context`](#discovery-data-plane) on the data plane.)

| Scope | Endpoint | |
|---|---|---|
| `usage.read` | `GET /integration/v1/usage?from=&to=&granularity=day\|month&groupBy=key,model,step&keyId=&runId=` | Requests, successful requests, tokens and cost in USD of **this integration's keys only** (credential and assigned, revoked ones included) per Europe/Stockholm day or month; `from` and `to` inclusive dates, at most 366 days; `groupBy` any of `key` (default), `model`, `step`, comma-separated, empty for one row per period. As in the Data API, a month granularity adds the monthly totals for months before the oldest kept record (they have no step); days and `runId` cover kept records only. Another integration's `keyId` is 404 |
| `usage.read` | `GET /integration/v1/budgets?keyId=` | The active budgets that apply to one of its keys, as the gateway applies them (the key's own and its rotation predecessors', its team's, its department's): scope, unit, limit, residency, period and its current window, `spent` in the budget's unit, `percentUsed`, `exhausted` |

Not offered: creating or changing keys, setting budgets, other keys' usage. Keys and budgets are made in the gateway
by the people responsible for the money.

## Reports and the Data API

- Nothing new is needed to follow cost: an assigned key is a key, so usage per key, team and department, budgets,
  alerts and the CSV export already show each chatbot. The key list gets an *integration* column and filter; the
  integration page lists its keys and their use per department.
- Data API (phase 2, built): `usage/records` gains `viaKeyId`, `runId` and `step` (appended, also as the last CSV
  columns); `catalog/keys` gains `integrationId` and `integrationRole`. The admin API's request list
  (`/api/usage/requests`) has the same three fields. Not built: `catalog/integrations` and a model's
  `maxOutputTokens` on the Data API (a platform reads them from `/v1/ume/context` and `/v1/models`).
- `DataApi:MinimumGroupSize` counts distinct keys per department. Each chatbot is a key, so a department with five
  chatbots is no longer folded as it would be behind one shared platform key.

## Data model

| Migration | Change |
|---|---|
| `Integrations` (phase 1, built) | table `Integrations`; `VirtualKeys`: `IntegrationId uuid NULL` (foreign key, indexed), `IntegrationRole varchar(20) NOT NULL DEFAULT 'None'`, `IntegrationScopes text[] NOT NULL DEFAULT '{}'`; `ModelDeployments.MaxOutputTokens integer NULL` |
| `UsageRunMetadata` (phase 2, built) | `UsageRecords`: `ViaKeyId uuid NULL`, `RunId varchar(100) NULL`, `Step varchar(100) NULL` (nullable columns: no table rewrite; not kept in monthly totals) |

`app-roles.sql`: the gateway reads `Integrations`, the admin API changes it (phase 1); `ume_data` reads the key
columns `IntegrationId` and `IntegrationRole` (phase 2). `UsageRecords` is granted whole to every role already.
No new budget scope, no change to the retention function, no new Redis keys.

## Request path cost

| Addition | Per request |
|---|---|
| `x-ume-on-behalf-of` | one header lookup; one frozen-dictionary lookup when present |
| Rate limit and budget | unchanged: the same counters as any key's request, in the same single round trip |
| Usage record | three nullable columns more in the batched COPY, off the request path |
| Stream cost | opt-in: one write after the last event |
| Discovery | not on the request path; 304 on an unchanged catalogue |

Phase 1 adds only two field checks to authentication and a join on a cold key load: the benchmarks' regression
`check` (2026-11-11) shows the same Postgres statements (9 on a key and catalogue cache miss, 0 warm) and Redis
commands (8) per request as the baseline, and no more allocations. Phase 2 (the header lookups, the run metadata
and the via key on the usage record) shows the same in its `check` run on 2026-11-12: 50.5 KB per warm chat, 0
Postgres statements and 8 Redis commands, as before.

## What a platform changes (Ume.Rag as the example)

| Today | With this |
|---|---|
| Organisation and model sync read the Data API with a Data API key | read `/v1/ume/context` and `/v1/models` with a credential key; the Data API key and network path go away |
| Per-level keys of the pilot team in each service | one credential key per service, with the residencies that service may ever reach (an upper bound); the ceiling header stays on every call |
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
OpenAPI 3.1 document, [openapi/integrations.json](openapi/integrations.json), and changed by addition only within
`v1` (new fields, features, scopes and enum values; a client ignores what it does not know), so a platform's fake
gateway can be tested against it. The gateway's own tests hold the real answers to the document
(`IntegrationContract` in the test kit): a field that is not described, or a described one that is missing, fails
the build.

## Phases

Each is additive and safe for a rolling update ([zero-downtime-updates.md](zero-downtime-updates.md)).

| Phase | Content | Done when |
|---|---|---|
| 1 Consolidate and share (built) | `Integrations`, key roles and scopes, admin UI, `MaxOutputTokens`, `/v1/models` facts, `/v1/ume/context`, ETag | a platform runs with gateway keys only, fills its pickers from the gateway, and the Data API can be switched off |
| 2 Keys per chatbot (built) | `x-ume-on-behalf-of`, echo, event 1006, run metadata, stream cost | two departments' chatbots show as their own keys in the usage report, and a key budget stops one of them with 402 |
| 3 Read back (built) | Integration API (`usage.read`) | the platform shows the gateway's cost and budget status per chatbot |
| 4 Later | webhooks | – |

## Decisions

Recorded as ADR 020 and 021 in [adr.md](adr.md).

## Open points

- A platform-wide cap: acted requests count against the assigned key's limits only. Not needed by Ume.Rag for now
  (knowledge bases have their own keys and the worker limits itself). If one is needed (so indexing cannot starve
  chat), the credential key's concurrency limit can be counted too, in the same script.
- Whether `pricing` on `/v1/models` should list every eligible target instead of the first (a fallback can cost
  differently; the actual cost is on the answer either way). Built: the first.
- `x-ume-stream-cost` with the Anthropic and Responses stream shapes: verify with the SDKs first.
