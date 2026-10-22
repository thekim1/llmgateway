# Introduction

The Ume AI Gateway sits between an organisation's applications and the language models they use. Applications keep
the OpenAI or Anthropic SDK they already have, point it at the gateway and use a **virtual key** instead of a provider
key. The gateway decides which provider serves the request, checks the key's limits and budget, and records what the
request cost.

It is a demonstrator by Umeå kommun, **not a production approval, legal compliance attestation or WCAG certification**.

## What it does

- **Virtual keys** per application, owned by a team in a department, with allowed models, residencies, client addresses,
  rate limits (sliding per-minute windows and a cap on requests in progress) and an expiry. Secrets are shown once;
  rotation has a grace period.
- **Request policies** per team and key: a cap on output tokens, a system prompt placed before the application's own,
  and parameter defaults or forced values.
- **Budgets** per department, team and key, with alerts at your thresholds. Requests stop when a budget runs out.
  Budgets count USD, or tokens: a token quota shares on-prem capacity (whose models cost nothing) between teams.
  Prices are configured in USD per model.
- **Routing**: applications ask for an alias such as `ume/chat-standard`; routes and [routing rules](routing-rules.md)
  (conditions on headers, team, budget use and personal data, with weights, fallbacks and chaining) decide where it goes.
  The `x-ume-rule` response header shows which rule applied.
- **Fallback, retries and circuit breaking** between providers, only within the residencies a key allows (on-prem, EU,
  external). A provider's `Retry-After` puts that deployment on cooldown; prompts too large for a model skip it.
- **Prompt caching**: Anthropic cache markers are added to translated requests, and `x-ume-session` keeps a conversation
  on one server so its prompt cache is reused.
- **Personal data**: an optional per-key [PII policy](personal-data.md) that detects Swedish personnummer, samordningsnummer,
  email addresses, phone numbers and IBANs, then records, redacts, blocks or keeps the request on-prem.
- **Attached files, speech to text, image generation and live audio** go through the same pipeline as chat.

## What it stores

Usage metadata only: time, key, team, department, model, provider, tokens, cost, latency, status and outcome. **Prompts
and responses are never stored or logged.** Providers and models are configuration, not hardcoded model lists.

## How it is built

A .NET 10 data plane (the gateway) and an OIDC/BFF control plane (the admin API), a Vue 3 admin UI, PostgreSQL and
authenticated Redis, orchestrated locally with Aspire 13.6. The [architecture](architecture.md) page describes the
components and trust boundaries; [performance](performance.md) describes what a request costs.

## Where to go next

- [Local development](local-development.md) runs the whole stack on a laptop with demo data and a fake LLM.
- [Production setup](production-setup.md) deploys a release bundle with Docker Compose.
- [Routing rules](routing-rules.md) keeps personal data on-prem with one rule.
