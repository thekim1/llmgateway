# Umeå kommun LLM gateway POC

**Website and documentation: <https://thekim1.github.io/llmgateway/>**

One gateway between an organisation's applications and every language model: virtual keys that teams can manage themselves, department/team/key budgets (in USD, or token quotas for on-prem capacity),
routing rules that keep personal data on-prem, residency restrictions, provider fallback and retries, request policies
(output caps, system prompts) per team and key, prompt caching and a per-key policy for attached files. Applications keep the OpenAI or Anthropic SDK they already use: the gateway translates where a model needs another API
(Claude through Chat Completions, tool calls on gpt-5.6/GPT-6 through the Responses API). The gateway stores usage metadata, **never prompt
or response content**. Chat, Responses, Messages, embeddings, speech to text, images and live audio all go through the
same pipeline.

.NET 10 data plane and OIDC/BFF control plane, Vue 3/Pinia admin UI, PostgreSQL, authenticated Redis and Aspire 13.6.
This is a demonstrator, **not a production approval, legal compliance attestation or WCAG certification**.

## Quick start

Windows with .NET SDK 10.0.401, Aspire 13.6, Docker 28+ and Node 24.15+ (PowerShell):

```powershell
Set-Location src\admin-ui
npm ci
Set-Location ..\..
.\scripts\Start-Dev.ps1          # with demo data (default)
.\scripts\Start-Dev.ps1 -Empty   # empty database, only the Keycloak login users
```

Open `https://localhost:5173` and sign in as `gateway-admin`. Details, Visual Studio, the fake LLM and the tests:
[Local development](docs/local-development.md).

Production, on a Linux Docker host with an OIDC client for the admin UI ([Production setup](docs/production-setup.md)).
Up to about 50 messages/s, one host with 2 vCPU, 4 GB RAM and 20 GB disk plus usage data runs everything
([Sizing](docs/production-setup.md#sizing)):

```bash
curl -fsSLO https://github.com/thekim1/llmgateway/releases/download/vX.Y.Z/ume-llm-gateway-X.Y.Z.tar.gz
tar -xzf ume-llm-gateway-X.Y.Z.tar.gz && cd ume-llm-gateway-X.Y.Z
./deploy/init-deployment.sh        # asks for the OIDC authority and public hostnames
docker compose -f deploy/compose.prod.yaml --env-file deploy/.env up -d --wait
```

In the admin UI, search (Ctrl K) finds any page or setting by name or topic, and on a phone **Menu** in the bottom bar opens
every page ([Admin UI](docs/admin-ui.md#finding-your-way)). *Usage > Requests and costs* with *Outcome: All errors* lists the
failed requests, and every error count links there ([Finding failed requests](docs/admin-ui.md#finding-failed-requests)). *Settings > Version* shows the running version and says when a newer release is out (the admin API checks
GitHub; set `Admin__CheckForReleases=false` without internet access). Model prices for Anthropic, Ollama Cloud, OpenAI and
OpenRouter follow the [models.dev](https://models.dev) list, updated daily, which also fills in empty descriptions,
capabilities and context windows; turn it off per model or provider to set your own prices
([List prices](docs/providers-and-models.md#list-prices); `Admin__PriceListUrl=` turns it off).
Upgrades: [Upgrade guide](docs/upgrade-guide.md).

## Build and test

Stop the running AppHost before backend or AppHost builds:

```powershell
aspire stop --apphost src\Ume.LlmGateway.AppHost\Ume.LlmGateway.AppHost.csproj --non-interactive
dotnet build UmeLlmGateway.slnx -v q -nologo
dotnet test --solution UmeLlmGateway.slnx
Set-Location src\admin-ui
npm run lint
npm run typecheck
npm test
```

The npm projects (`src/admin-ui`, `site`, `tests/e2e`) do not run package install scripts and skip package versions
younger than 7 days ([npm packages](docs/security-and-compliance.md#npm-packages)).

Browser tests, benchmarks and the pre-commit hook: [Local development](docs/local-development.md#build-and-test) and
[Performance](docs/performance.md#measuring).

## Documentation

Every page below is also on the [website](https://thekim1.github.io/llmgateway/docs/), built from these files by
`site/` (see [Website](docs/local-development.md#website)).

| Get started | Guides | Operate and reference |
|---|---|---|
| [Introduction](docs/introduction.md) | [Routing rules](docs/routing-rules.md) | [Runbook](docs/runbook.md) |
| [Local development](docs/local-development.md) | [Personal data (PII)](docs/personal-data.md), [Content guard](docs/content-guard.md) | [Upgrade guide](docs/upgrade-guide.md) |
| [Production setup](docs/production-setup.md) | [Attached files](docs/attached-files.md) | [Data retention and logging](docs/data-retention.md) |
| [Kubernetes](docs/kubernetes.md) | | [Updates without downtime](docs/zero-downtime-updates.md) |
| | [Speech to text](docs/speech-to-text.md) | [Security events](docs/security-events.md) |
| | [Image generation](docs/image-generation.md) | [Data API](docs/data-access.md) |
| | [Live audio](docs/live-audio.md) | [Admin API](docs/admin-api.md) |
| | [Providers and models](docs/providers-and-models.md) | [Architecture](docs/architecture.md) |
| | [Admin UI](docs/admin-ui.md) | [ADRs](docs/adr.md) |
| | [Client addresses](docs/client-addresses.md) | [Security and compliance](docs/security-and-compliance.md) |
| | | [Accessibility statement](docs/accessibility-statement.md) |

Benchmarks: [Performance](docs/performance.md), [Streaming findings](docs/streaming-findings.md) and the
[platform comparison](docs/reports/platform-comparison.pdf).

Changes per release: [Changelog](CHANGELOG.md). For maintainers (not on the website): [Handover](HANDOVER.md),
[Performance improvement plan](docs/performance-improvement-plan.md),
the [Demo script](docs/demo-script.md) and the [comparison with agentgateway](docs/agentgateway-comparison.md).
