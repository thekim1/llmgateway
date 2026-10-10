# Umeå kommun LLM gateway POC

.NET 10 data plane and OIDC/BFF control plane, Vue 3/Pinia admin UI, PostgreSQL,
authenticated Redis and Aspire 13.6. This is a demonstrator, **not a production approval,
legal compliance attestation or WCAG certification**.

The gateway provides virtual keys, department/team/key budgets in SEK, provider fallback,
residency restrictions, optional PII policy and a per-key policy for attached files. It stores usage metadata, never prompt or
response content. Providers/models are configuration, not hardcoded model-family lists.
Routing rules (conditions on headers, team, budget use and PII that rewrite where a request goes,
with weights, fallbacks and chaining) are applied to live requests and described in
[Routing rules](docs/routing-rules.md). The `x-ume-rule` response header shows which rule applied. Administrators manage rules through the admin API
(create, validate, dry-run, reorder, reassign); deleting a team or department that has rules asks whether to delete
the rules or deactivate them until a new team or department is assigned.

## Local development

Windows: .NET SDK 10.0.401, Aspire 13.6, Docker 28+, Node 24.15+ recommended.
Use PowerShell 5.1 and run commands separately (or chain with `;` and exit-code checks).

```powershell
Set-Location src\admin-ui
npm ci
Set-Location ..\..
.\scripts\Start-Dev.ps1          # with demo data (default)
.\scripts\Start-Dev.ps1 -Empty   # empty database, only the Keycloak login users
```

The script wraps `aspire start` + `aspire wait admin-ui`; you can still run those directly
(demo data is then on unless `Seed__Enabled=false` is set in the environment).

### Demo data or an empty environment

Seeding (`Seed:Enabled`) runs in the migration service and **only when the database has no
departments**. Demo data is fictional förvaltningar, teams, providers, models, prices, routes and
a dev key and three example routing rules (see [Routing rules](docs/routing-rules.md)). In the empty variant you sign in with a Keycloak test user (`gateway-admin`) and create
everything yourself. To switch an existing local environment, stop the stack, delete the
AppHost's Postgres Docker volume (`docker volume ls`, then `docker volume rm <name>`; or the
volume named in `DevelopmentVolumes:Postgres`) and start again.

### Fake LLM toggle

The fake provider (`fake-llm`) starts with the AppHost by default. Set `"FakeLlm:Enabled": false`
in the AppHost user secrets (or `FakeLlm__Enabled=false` in the environment) to skip it. The admin UI
badge shows **Development** or **Production** from the admin API's environment.

### Running from Visual Studio

1. Install Visual Studio 2022 17.14+ (or newer) with the .NET 10 SDK, Docker Desktop (running), Node 24+, and run `dotnet dev-certs https --trust` once.
2. Open `UmeLlmGateway.slnx`, set `Ume.LlmGateway.AppHost` as startup project, profile **https**.
3. Right-click the AppHost project > **Manage User Secrets** and paste the dev secrets JSON
   (`Parameters:*` for pepper, dev-key, OIDC secret, dev user password, Redis and **Postgres**
   passwords, plus `DevelopmentVolumes:Postgres`). Keep them stable: the Postgres volume stores the
   password it was created with, and the key pepper hashes all virtual keys, so changing either
   later breaks the existing data. With these set, data survives restarts and `git pull`
   (migrations run automatically). Aspire generates the passwords itself if you skip this step.
4. `"Seed:Enabled": false` = empty environment; `true` or omitted = demo data. It only takes effect on
   an empty database (delete the Postgres Docker volume to switch).
5. Press F5. The Aspire dashboard opens; click the `admin-ui` endpoint. Keycloak test users are
   created from `dev/keycloak`; the shared dev password is the `dev-user-password` secret.

### Environments (dev / test / production)

There is no separate "test" configuration; the mode decides the environment:

| Environment | How | Identity | Data |
|---|---|---|---|
| Development | `aspire start` / `Start-Dev.ps1` (run mode, `Development`) | Keycloak test realm (optional: set `Oidc:Authority` in AppHost user secrets to use an existing provider), FakeLlm, optional Ollama | Demo or empty, see above |
| Test (production-shaped) | `.\deploy\Test-Deployment.ps1` (isolated, throw-away) | Test-only secrets | Empty, never seeded |
| Production | `deploy/compose.prod.yaml` after `deploy/init-deployment.sh` (`ASPNETCORE_ENVIRONMENT=Production`) | External HTTPS OIDC | Never seeded (`Seed__Enabled=false`); no Keycloak or FakeLlm |

Production inputs (OIDC authority/client, certificates, secrets, provider endpoints) are
generated or prompted for by `deploy/init-deployment.sh` (see *Quick production setup* below) and
described in [the runbook](docs/runbook.md). Other settings can be overridden
with `Section__Key` environment variables (for example `Ollama__Enabled=true`, development only).

### Admin UI

`src/admin-ui` is Vue 3 + Pinia + Vue Router with Reka UI primitives and Tailwind CSS v4, built from the design prototype in
`Design prototype/` (tokens in `src/admin-ui/src/styles/tokens.css`, Tailwind config in
`tailwind.config.cjs`). The UI is English only, with light, dark and Lumen themes
(`data-theme`, remembered in `localStorage`). Reusable building blocks live in
`src/components/ui` (buttons, tables, drawers, dialogs, form fields, meters), the page shell in
`src/components/layout`, and pages in `src/views`. Icons are a subset of Material Symbols; after
adding an icon name run `python src/admin-ui/scripts/subset-icons.py` (it needs the Python package `fonttools[woff]`).

#### Routing rules page

**Routing > Routing rules** (gateway-admin) manages [routing rules](docs/routing-rules.md): rules are listed by
scope in the order the gateway checks them, with the selected rule's condition, weighted targets and fallbacks on the
right. From there you can create and edit rules (the condition is validated as you type), switch a rule off, move it
earlier or later, change its owner and delete it. **Test a request** runs a sample request against the stored rules and
shows which rule applies and why. When you delete a team or department in **Organisation** that has routing rules, the UI asks
whether to delete the rules or deactivate them until a new team or department is assigned; deactivated rules show up under
**Needs owner**. Usage shows which rule routed a request.

#### Discovering provider models

Instead of typing models in by hand, open a provider (**Providers & models**) and choose
**Discover models**. The Admin API calls the provider with its stored credential, so a
successful list also proves the connection and credential work. Pick the models to add and
the gateway creates them with what the provider reports:

| Provider | Listing endpoint | Reported details |
|---|---|---|
| OpenAI, OpenAI-compatible, Azure AI Foundry | `GET {baseUrl}/models` | context size and USD prices where present (OpenRouter-style) |
| Anthropic | `GET {baseUrl}/models` | context size and capabilities |
| Ollama, Ollama Cloud | `GET /api/tags` then `POST /api/show` (a trailing `/v1` is stripped from the base URL) | context size and capabilities (tools, vision, thinking, embedding, ...) |
| Azure OpenAI | not supported (deployments are not listable via the data plane) | add models manually |

Capabilities are stored on each model (`features`), shown in the Models table, and can be
edited in the model drawer. Most providers, including Ollama Cloud, do not publish prices, so
add them under the model's price history. Prices are in USD, so also enter the USD exchange rate under **Settings**:
until one is in effect the gateway uses `Gateway:FallbackSekPerUsd` (10 SEK), logs a warning and reports its readiness
check `exchange-rate` as Degraded (visible under operations health). Audio, image-generation-only and moderation models
are left out of the list. Models added before this feature have no capabilities until you set
them or re-add them through discovery. Database migration `ModelFeatures` adds the column and
runs with the normal migration service.

The UI is `https://localhost:5173`. Local Keycloak test users/roles are provisioned by
the realm import; get development passwords through Aspire's secret store without
printing them, enabling transcripts or saving browser auth state. Ollama is opt-in:
set `Ollama__Enabled=true` before starting. FakeLlm is the default test provider.
Stop **this exact AppHost** before backend/AppHost edits or builds:

```powershell
aspire stop --apphost src\Ume.LlmGateway.AppHost\Ume.LlmGateway.AppHost.csproj --non-interactive
dotnet build UmeLlmGateway.slnx -v q -nologo
dotnet test --solution UmeLlmGateway.slnx
Set-Location src\admin-ui
npm run lint
npm run typecheck
npm test
```

`npm run lint` fails on warnings, exactly like CI (`npm run lint:fix` fixes most). To catch it before
pushing, enable the pre-commit hook once per clone: `git config core.hooksPath .githooks`.
Debug builds of the admin API (Visual Studio, F5) also run the lint and show problems as a build warning
(skipped when `node_modules` is missing or `CI=true`).

Browser tests (`tests\e2e`, Playwright) need a running stack. Use the separate test database so your
development data is never touched:

```powershell
.\scripts\Start-E2E.ps1          # own Docker volume (ume-e2e-postgres), reset on every start, demo data, fake LLM on
Set-Location tests\e2e
npm ci
npx playwright install chromium
npx tsc --noEmit
npx playwright test
Set-Location ..\..
.\scripts\Start-E2E.ps1 -Stop    # stops the stack and removes the test database
```

The test stack uses the same ports as the dev stack, so stop the dev stack first. The tests cover sign-in, keys
(show-once secret, rotation, revocation), organisation, usage, audit, budgets, the demo scenario, routing
rules (create, validate, test, reorder, disable, delete, team and department deletion and reassignment, key rotation, `budget_used`/`tokens_used`, with real gateway
requests) and accessibility (axe at WCAG 2.2 AA in the three themes, drawers and dialogs, reflow, keyboard,
forced colours, target size). They do not save screenshots, videos, traces or storage state.
No manual screen reader testing is planned; open follow-ups are listed in [HANDOVER.md](HANDOVER.md).

### Benchmarks

The gateway's data plane is benchmarked in `benchmarks/Ume.LlmGateway.Benchmarks` (needs Docker; always Release):

```powershell
dotnet run -c Release --project benchmarks\Ume.LlmGateway.Benchmarks -- --filter *   # BenchmarkDotNet, ~20 min
dotnet run -c Release --project benchmarks\Ume.LlmGateway.Benchmarks -- audit        # DB/Redis calls per request + load test
```

`benchmarks/compare` compares the gateway with eneo against the same fake LLM (see its README).
Run them before and after changes to the request pipeline. What they measure and the current results are in
[Performance](docs/performance.md).

## Routing examples

Both examples are global rules created under **Routing > Routing rules** (gateway-admin) with the demo data. Your applications keep
asking for the same model name; the rules decide where the request goes. The screenshots were taken from the real UI and are
regenerated by `tests/e2e/docs/screenshots.spec.ts` (see below).

### Example 1: use another model if the main model doesn't answer

*Ask for `ume/chat-advanced`. If every provider behind it fails or times out, try `ume/chat-standard` instead.*

| Field | Value |
|---|---|
| Name | Advanced model with fallback |
| Applies to | Global |
| Condition | `model == "ume/chat-advanced"` |
| Send to | `ume/chat-advanced` (weight 1) |
| Fallbacks | 1. `ume/chat-standard` |

![The new rule form with the condition, the target and one fallback](docs/images/routing-fallback-form.png)

After **Create rule** the rule shows up in the list, in the order the gateway checks it:

![The saved rule: condition, target and "Then, if those fail" fallback](docs/images/routing-fallback-rule.png)

How it behaves: the target is tried first (with its own provider order from the route). The fallbacks are tried only when the call fails
in a way that can be retried (a timeout, 408, 409, 429, a 5xx or a provider configuration error), and never after the response has started
streaming. Other client errors (such as 400) are returned as they are. The response header `x-ume-fallbacks` counts the extra attempts and
`x-ume-rule` names the rule. Providers whose circuit breaker is open are moved last, so the fallback is tried before a known-broken primary.
Only transient failures (timeouts, connection errors, 408, 409, 429, 5xx) count toward the circuit breaker; a provider configuration
error (such as an upstream 401, 403 or 404) falls back to the next provider without opening the circuit.

### Example 2 (advanced): chats with personal data go to an on-prem model

*When the gateway finds personal data in a chat (a personnummer, an email address, a phone number, an IBAN), send the request to
`ume/chat-onprem` instead of an EU or external provider.*

| Field | Value |
|---|---|
| Name | Personal data stays on-prem |
| Applies to | Global |
| Condition | `pii_detected && endpoint == "chat_completions"` |
| Send to | `ume/chat-onprem` |
| Fallbacks | none, on purpose |

![The new rule form with the pii_detected condition and the on-prem route as target](docs/images/routing-pii-form.png)

Three things make this safe:

1. **Order matters: the first matching rule wins.** New rules are added last, so use **Check earlier** (the arrow buttons next to *Order*) until the
   rule is number 1. Otherwise a request with personal data that also matches *Premium via header* would go to `ume/chat-advanced`.
2. **No fallbacks.** If the on-prem model is down the call fails with `503 no_eligible_provider` or `502 all_providers_failed` instead of
   quietly sending the chat to an EU or external provider. Add a fallback only if it is also on-prem.
3. **Rules never widen access.** A key's allowed providers and residencies are applied after the rules, so the rule cannot send a chat anywhere the
   key may not go.

![The rule at the top of the list, checked first](docs/images/routing-pii-rule.png)

Check the rule before relying on it with **Test a request**: ask for `ume/chat-standard`, open *Usage values* and set *Personal data* to
*Personal data found*:

![The Test a request dialog with "Personal data found" selected](docs/images/routing-pii-test-input.png)

The result says which rule applies, which models the request is tried against and why each condition was true or false:

![The test result: Personal data stays on-prem applies, request goes to ume/chat-onprem](docs/images/routing-pii-test-result.png)

On the real gateway (checked with the demo data and a synthetic personnummer): a chat containing `19121212-1212` was served by
`fake-onprem` (`x-ume-residency: OnPrem`, `x-ume-rule` set), while a chat without personal data went to the EU provider as before.

Notes:

- `pii_detected` is evaluated for every request that a rule asks about it, **whatever the key's PII policy** (Off, Allow, Redact), so the rule
  works even for keys that have no PII policy. If the key's policy is **Block**, the request is rejected before the rules run.
- If you only need "personal data stays on-prem" for specific keys, the key's PII policy **Reroute to on-prem** does the same without a rule.
  Use a rule when you want it per team or department, per model, with a chosen on-prem model, or combined with other conditions
  (for example `pii_detected && department_name == "Individ- och familjeomsorgen"`).
- Detection covers Swedish personnummer and samordningsnummer (with checksum), email addresses, Swedish phone numbers and IBANs (not bankgiro numbers).
  It is a safety net, not a guarantee that no personal data reaches a provider.

The same two examples are on the **Getting started** page of the admin UI:

![The Routing examples card on the Getting started page](docs/images/getting-started-routing.png)

To regenerate the screenshots after a UI change, start the test stack (`.\scripts\Start-E2E.ps1`) and run
`npx playwright test -c playwright.docs.config.ts` in `tests\e2e`. The script builds the two rules through the UI, so it
changes the (disposable) test database only.

## Attached files

Applications send images, PDFs, audio and video inline in the request body, as content parts
(`image_url`/`file`/`input_audio` in Chat Completions, `input_image`/`input_file` in Responses, `image`/`document` in
Anthropic Messages, and `video_url` for video models on vLLM and other OpenAI-compatible servers). The gateway has
no upload endpoint: a provider `file_id` only works with the one provider account it was uploaded to, which would
break alias routing and fallback.

Bodies are limited to 16 MB (`Gateway__MaxRequestBodyBytes`, at most 100 MB); larger requests get 413
`request_too_large`. Base64 adds a third, so this fits about 12 MB of files, enough for photos and PDFs but only short
video clips. Send longer video as a URL the model server can fetch, or raise the limit. Whether a model accepts a given
kind of file is up to the provider; Claude models take images, PDFs and plain text but not audio or video.

Budget reservations count each image as 1 600 input tokens instead of by its base64 size (a 4 MB photo used to
reserve about a million tokens and could hit 402 `budget_exceeded` on a small budget). Documents, audio and video are
still estimated from their size, which over-reserves; the actual cost is settled from the provider's reported usage.

Each key has an **Attached files** setting (Keys > edit key > Access):

| Setting | Effect |
|---|---|
| **All files** (`Allowed`, default) | Everything is sent on, as before. |
| **Images only** (`ImagesOnly`) | Images are sent on; documents, audio, video and `file_id` references are rejected. |
| **Text only** (`None`) | Any file is rejected. |

A refused request gets 400 `attachment_not_allowed`. The PII policy reads text, not file contents, so use **Text only**
for keys whose users handle data that must never reach a provider. A chat front-end that extracts a file's text itself
and sends it as an ordinary message is not stopped by this setting; the PII policy covers that text. Details:
[admin-api.md](docs/admin-api.md#gateway-data-plane--for-the-developer-portal-snippets).

Chat Completions requests to Claude models now carry PDF and plain-text files as Anthropic `document` blocks (they used
to be dropped silently). Parts Claude cannot take (audio, video, other file types, OpenAI `file_id`) return 400
`unsupported_content` instead of being left out of the prompt.

## Speech to text

Transcription models (Whisper, KB-Whisper, gpt-4o-transcribe and others with an OpenAI-compatible API, such as vLLM,
Speaches or Azure OpenAI) are served at `POST /v1/audio/transcriptions`, and English translation at
`POST /v1/audio/translations`. Clients use the OpenAI SDKs unchanged:

```bash
curl https://<gateway>/v1/audio/transcriptions -H "Authorization: Bearer $UME_API_KEY" \
  -F model=ume/transcribe -F language=sv -F file=@samtal.mp3
```

**Setting it up.** Give the provider the *Speech to text* capability (`AudioTranscriptions`), add the model with kind
*Speech to text* (`Transcription`; discovery does this for `whisper*` and `*-transcribe` models) and put it behind an
alias with the same kind. Fallback, residency, provider allow-lists, rate limits, budgets and routing rules
(`endpoint == "audio_transcriptions"`) work as for chat. The dev seed has `ume/transcribe` on FakeLlm.

**Prices.** Whisper-style models are priced per minute of audio (*Audio (USD per minute)* in the price form,
`audioPerMinuteUsd`); gpt-4o-transcribe per token, with audio tokens dearer than text tokens (*Audio input (USD per 1M
tokens)*, `audioInputPerMillionUsd`; empty bills them at the input price). The duration comes from the provider
(`usage.seconds`, or `duration` in `verbose_json`); when it is not reported, the gateway reads it from the file (exact
for WAV and FLAC, from the bitrate for MP3, assumed 32 kbps otherwise, which over-estimates). Usage records and the
Data API have the new `audioSeconds` field; migration `AudioTranscription` adds it.

**Limits and privacy.** Uploads may be 26 MB (`Gateway__MaxAudioRequestBodyBytes`, up to 512 MB for on-prem servers
taking longer recordings; raise the proxy's `client_max_body_size` with it). The recording is held in memory for the
duration of the request only, never written to disk or logged, and sent again from memory on fallback. A key whose
**Attached files** setting is *Images only* or *Text only* cannot send audio (`attachment_not_allowed`). The PII policy
scans the `prompt` field but cannot hear the recording or read the transcript, so use *Text only*, an on-prem-only
alias or allowed residencies for recordings with personal data.

## Live audio (realtime)

Live transcription, live interpreting and realtime sessions use WebSockets with the
[OpenAI Realtime protocol](https://platform.openai.com/docs/guides/realtime), relayed to the provider:

| Endpoint | Use | Model kind | Dev seed |
|---|---|---|---|
| `wss://<gateway>/v1/realtime?model=<alias>` | Live transcription (speech-to-text models such as gpt-realtime-whisper, gpt-4o-transcribe, Whisper on vLLM) | *Speech to text* (`Transcription`) | `ume/live-transcribe` |
| `wss://<gateway>/v1/realtime?model=<alias>` | Realtime sessions with a model (gpt-realtime, Foundry Voice Live) | *Realtime (live)* (`Realtime`) | `ume/realtime` |
| `wss://<gateway>/v1/realtime/translations?model=<alias>` | Live interpreting into another language (gpt-realtime-translate) | *Live interpreting* (`SpeechTranslation`) | `ume/interpret` |

Connect **from a backend** with the key in the `Authorization` header (browsers cannot set it; let them talk to your
backend). Then send events as with OpenAI: `session.update`, audio as base64 24 kHz 16-bit mono PCM in
`input_audio_buffer.append` (`session.input_audio_buffer.append` for interpreting), and read the transcript events.
The Getting started page has Python, .NET and Node.js examples for the selected alias.

```python
async with websockets.connect("wss://<gateway>/v1/realtime?model=ume/live-transcribe",
                              additional_headers={"Authorization": f"Bearer {key}"}) as ws:
    await ws.send(json.dumps({"type": "session.update", "session": {"type": "transcription",
        "audio": {"input": {"transcription": {"model": "ume/live-transcribe", "language": "sv"}}}}}))
    await ws.send(json.dumps({"type": "input_audio_buffer.append", "audio": base64.b64encode(chunk).decode()}))
```

**What the gateway does.** At connect, the usual pipeline runs once: key, attachment policy (audio must be allowed),
alias, rate limit, routing rules (`endpoint == "realtime"` / `"realtime_translations"`), residency, budget reservation,
and the provider connection, with fallback to the next provider if it refuses. Every refusal is a plain HTTP error
before the upgrade. During the session the gateway passes events through and only reads what it needs:

- **Model names stay the gateway's.** Any `model` in `session.update` is replaced by the routed upstream model. A
  realtime session may ask for input transcription with a speech-to-text alias; it must be served by the same provider
  and is billed at its own price.
- **Metering.** Audio seconds are counted from the appended audio (the session's input format gives the rate), and
  token usage is read from `response.done` and transcription events. Audio tokens are billed at the model's audio token
  prices when set, duration at its per-minute price. Interpreting is billed by the counted duration.
- **Budgets.** Five minutes of audio are reserved at connect and again whenever the reservation runs low; when a budget
  runs out the client gets an `error` event (`budget_exceeded`) and the session is closed.
- **PII.** Text in `session.update`, `conversation.item.create` and `response.create` goes through the key's PII
  policy: blocked or redacted as for chat, and refused (not rerouted) when it may only go on-prem but the session runs
  elsewhere. Audio itself cannot be scanned; choose on-prem or EU aliases for conversations with personal data.
- **Limits.** 20 open sessions per key across instances, 120 minutes per session, 4 MB per event
  (`Gateway__Realtime__*`, see [admin-api.md](docs/admin-api.md#live-audio-realtime)). Binary frames are refused.

A session is one usage record (endpoint `Realtime` or `RealtimeTranslations`): `latencyMs` is the session length and
`audioSeconds` the audio streamed. Audio is never stored or logged.

**Providers.** OpenAI (`https://api.openai.com/v1`), Azure OpenAI and Azure AI Foundry (`https://<resource>.openai.azure.com/openai/v1`
or `…services.ai.azure.com/openai/v1`), Foundry Voice Live for other models in Sweden
(`https://<resource>.services.ai.azure.com/voice-live?api-version=2026-04-10`; base URLs may now carry query parameters,
never credentials), and on-prem servers with the same protocol such as vLLM and Speaches (`http://<server>/v1`). Give
the provider the *Live audio (realtime)* capability. Behind nginx, pass the WebSocket upgrade (see
`deploy/proxy/nginx.conf.example`).

## Security events and data for other teams

Security teams, BI and management get the gateway's data through interfaces, never through its database. The plan
and the reasons are in [data-access.md](docs/data-access.md). What exists today:

- **Security events for your SIEM.** The gateway and admin API log security events under the category
  `Ume.LlmGateway.Security` with stable event names: `gateway.auth.failed`, `gateway.pii.action`,
  `gateway.request.refused` and `admin.change`. Set `OTEL_EXPORTER_OTLP_ENDPOINT` on both services and route that
  category to your SIEM with an OpenTelemetry Collector.
  [`deploy/otel/collector-siem.example.yaml`](deploy/otel/collector-siem.example.yaml) sends it to syslog (RFC 5424),
  with Splunk, Elastic and Sentinel alternatives.
- **Refused keys are recorded.** Missing, unknown, revoked, expired and disabled keys are counted in the new
  `AuthFailures` table. A row covers one reason, endpoint, key and client network for up to 10 seconds, so callers
  without a valid key cannot flood the database. The presented key is never stored. Refused keys on `GET /v1/models`
  are recorded with the endpoint `Models`.
- **Unknown keys are cached.** A key the gateway does not know is remembered for `Gateway:KeyCacheSeconds` (30 s), so a
  client retrying with a deleted or mistyped key costs no database query (0.99 → 0.12 ms per refused request in the
  `audit` benchmark). Creating a key in the admin API clears the cache at once.

Settings (`Gateway:Security:*`):

| Setting | Default | |
|---|---|---|
| `SourceAddress` | `Truncated` | Client address kept for refused keys: `Truncated` (IPv4 /24, IPv6 /48), `Full` or `None`. Behind a reverse proxy, also set `UME_FORWARDED_HEADERS=true` (`ASPNETCORE_FORWARDEDHEADERS_ENABLED`). |
| `AuthFailureFlushSeconds` | `10` | How often refused keys are written and logged. |
| `MaxAuthFailureBuckets` | `1000` | Distinct rows per interval; anything beyond is folded into one row per reason and endpoint. |

### Data API for BI, management and security

An optional, read-only HTTPS service (`Ume.LlmGateway.DataApi`) for integrations: a data warehouse ETL job, a
management dashboard, a SIEM connector. It never gives anyone database access.

- **Each integration is an OAuth client** (client credentials) at your identity provider and gets only the
  permissions it needs: `usage.aggregate` (totals per day or month and department, model, provider), `usage.detail`
  (one row per request), `security.read` (refused keys, PII actions, audit log, key inventory) or `catalog.read`
  (departments, teams, keys, models, prices, budgets). Works with Keycloak, AD FS and Entra ID.
- **Incremental feeds**: pass the `nextCursor` of one page as `after` on the next call. JSON, or `format=csv` /
  `format=ndjson` for bulk loads.
- **Privacy by default**: departments with fewer than 5 active keys in a period are reported together without a
  department, and per-request PII categories are left out of `usage.detail`.
- **Read-only by database role**: it connects as `ume_data`, which cannot read key hashes, encrypted secrets or
  provider credentials at all. Every read is logged as a `data.read` security event.

Locally it starts with the rest of the stack (resource `dataapi`, `https://localhost:7311/scalar`). Get a token from the
bundled Keycloak with the `ume-data-dev` client (all four permissions); its secret is the AppHost parameter
`data-client-secret`:

```powershell
$token = (Invoke-RestMethod -Method Post "<keycloak-url>/realms/ume/protocol/openid-connect/token" -Body @{
  grant_type = 'client_credentials'; client_id = 'ume-data-dev'; client_secret = '<data-client-secret>' }).access_token
Invoke-RestMethod "https://localhost:7311/v1/usage/records?limit=10" -Headers @{ Authorization = "Bearer $token" }
```

In production it is the Compose profile `data` (`COMPOSE_PROFILES=data`); see the runbook, *Data API (optional)*,
and for existing installations the upgrade guide. Endpoints and design: [data-access.md](docs/data-access.md#phase-2-data-api).

## Deployment and documentation

### Quick production setup

On a Linux Docker host (Docker 28+, Compose v2.24+), with the three images built and pushed
(runbook, *Build and publish*) and an OIDC client for the admin UI:

```bash
./deploy/init-deployment.sh        # asks for the OIDC authority and public hostnames
docker compose -f deploy/compose.prod.yaml --env-file deploy/.env up -d --wait
```

`init-deployment.sh` creates an internal CA, service certificates, the Data Protection
certificate, all database/Redis passwords, the key pepper and `deploy/.env`
(non-secret settings, `GATEWAY_IMAGE`/`ADMINAPI_IMAGE`/`MIGRATIONS_IMAGE` overrides).
It is safe to re-run: existing secrets are never overwritten. All options are available
non-interactively (`--help`); `--renew-certs` re-issues the service certificates. Back up
`deploy/secrets/.pepper` and `deploy/certs/data-protection.pfx` separately from database dumps.

No shell access (**Portainer**)? Use `deploy/compose.portainer.yaml`: the same stack configured only through
environment variables, with certificates and secrets generated automatically in Docker volumes on
first start. See the runbook, *Portainer and other hosts without shell access*.

Behind **Nginx Proxy Manager, nginx or an F5** that terminates TLS: add `--proxy` (two hostnames,
an nginx config is generated). With certificates from your **PKI**: pass `--gateway-cert`,
`--gateway-key`, `--admin-cert`, `--admin-key` (and `--ca-file`). Details and the Nginx Proxy
Manager settings are in the runbook, *Reverse proxy and PKI*.

Production never uses the bundled Keycloak: point `UME_OIDC_AUTHORITY` at your existing Keycloak, AD FS or Entra ID.
Roles, department codes and AD groups are mapped by claims; see the runbook, *Identity provider*.

`deploy/compose.prod.yaml` is the recommended, self-contained production file (hardened,
read-only, non-root, internal data network). The older route, `aspire publish -o deploy\generated`
plus `compose.hardening.yaml`, is what `.\deploy\Test-Deployment.ps1` verifies; it is kept in sync
with the new file.

The Aspire-generated alternative: generate Compose with
`aspire publish -o deploy\generated --non-interactive`.
**Never deploy the generated base alone**: use the mandatory hardening override and
operator-mounted secrets/certificates in [the runbook](docs/runbook.md).
`.\deploy\Test-Deployment.ps1` builds and verifies an isolated production-shaped stack
with RAM-only test secrets and cleans up only its own resources.

Open `UmeLlmGateway.slnx` in Visual Studio and select `Ume.LlmGateway.AppHost` as
the startup project. Its `Properties\launchSettings.json` contains the HTTPS/HTTP
dashboard profiles; root `aspire.config.json` selects the same project for CLI commands.
The original development secret-store ID is preserved. When migrating an existing
checkout, set `DevelopmentVolumes:Postgres` (and `DevelopmentVolumes:Ollama`, if used)
in AppHost user secrets to the previous Docker volume names before starting, so existing
development data is reused. These optional overrides apply only in local run mode.

| Document | Purpose |
|---|---|
| [Admin API](docs/admin-api.md) | UI/API contract |
| [Routing rules](docs/routing-rules.md) | Rule model, condition language, evaluation order, examples (see also *Routing examples* above) |
| [Architecture](docs/architecture.md) | Scope and decisions, components, trust boundaries, tradeoffs |
| [Performance](docs/performance.md) | Per-request cost (Postgres/Redis round trips, caching), benchmarks and how to run them |
| [Performance improvement plan](docs/performance-improvement-plan.md) | Prioritised bottlenecks with evidence and acceptance criteria (handoff) |
| [Security and compliance](docs/security-and-compliance.md) | Controls, DPIA and processing-record inputs, release gates |
| [Accessibility statement](docs/accessibility-statement.md) | Automated evidence and outstanding manual checks |
| [Runbook](docs/runbook.md) | Deployment, credentials, backups, incidents |
| [Upgrade guide](docs/upgrade-guide.md) | Migration, rollback, certificates |
| [ADRs](docs/adr.md) | Key design decisions |
| [Demo script](docs/demo-script.md) | Safe repeatable demonstration |
| [Handover](HANDOVER.md) | Current status, open items, conventions and gotchas |
| [Changelog](CHANGELOG.md) | Changes per release |
