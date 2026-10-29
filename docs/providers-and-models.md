# Providers and models

## What a provider serves

The provider type decides which gateway endpoints a provider serves; there is nothing to tick. Which **model** gets a
request then follows from the model's kind (an embedding model gets `/v1/embeddings`, a speech-to-text model
`/v1/audio/transcriptions`, and so on).

| Provider type | Serves |
|---|---|
| OpenAI, Azure OpenAI, Azure AI Foundry | chat completions, Responses, embeddings, speech to text, live audio (realtime), image generation and editing, streaming (Claude on Foundry: chat completions and Anthropic Messages) |
| Anthropic | chat completions (translated), Anthropic Messages, streaming |
| Ollama | chat completions, embeddings, streaming |
| Ollama Cloud | chat completions, streaming |
| OpenAI-compatible | the endpoints you choose under *Endpoints this server offers* (default chat completions, streaming and embeddings), since they depend on the server (vLLM, LiteLLM, Speaches, an on-prem speech server) |

## Discovering models

Instead of typing models in by hand, open a provider (**Providers & models**) and choose
**Changing a provider's address.** The credential is write-only and tied to the address it was entered for: when you
change the base URL to another scheme, host or port, enter the credential again (or remove it). Another path on the same
host keeps it.

**Discover models**. The Admin API calls the provider with its stored credential, so a
successful list also proves the connection and credential work. Pick the models to add and
the gateway creates them with what the provider reports:

| Provider | Listing endpoint | Reported details |
|---|---|---|
| OpenAI, OpenAI-compatible | `GET {baseUrl}/models` | context size and USD prices where present (OpenRouter-style) |
| Azure AI Foundry | `GET https://<resource>.services.ai.azure.com/api/projects/<project>/deployments?api-version=v1` (only the deployed models, by deployment name) | model, version, capabilities and a suggested USD price from Azure's list prices |
| Anthropic | `GET {baseUrl}/models`; an Anthropic provider at a Foundry project endpoint lists the project's Claude deployments | context size and capabilities |
| Ollama, Ollama Cloud | `GET /api/tags` then `POST /api/show` (a trailing `/v1` is stripped from the base URL) | context size and capabilities (tools, vision, thinking, embedding, ...) |
| Azure OpenAI | not supported (deployments are not listable via the data plane) | add models manually |

For Azure AI Foundry, paste the **project endpoint** from the Foundry portal (`https://<resource>.services.ai.azure.com/api/projects/<project>`)
as the base URL. It is saved as entered, because discovery needs the project: `/openai/v1/models` would list every model the
resource *could* deploy, so discovery asks the project API for the deployments instead, and a model is added under its deployment
name, the name requests use. Requests go to the resource's `…/openai/v1`, which the gateway derives from the project endpoint.
A base URL without a project (`…/openai/v1` or the bare resource address) also works for requests, but discovery then has to use
Foundry's default project (`_project`), which not every resource has (404: enter the project endpoint). A Foundry provider set up
as *OpenAI-compatible* lists the whole catalogue.

**The same model from several providers.** A model's name is unique per provider (case-insensitively), so Foundry and
Ollama Cloud can both offer `DeepSeek-V4.1-Flash`. Clients call a model by its *public name*: the plain name while only one
provider has it, `<provider>/<model>` once several do (`<provider>/<model>` always works; the model list shows the public
name). A plain name several providers share is refused with `400 model_ambiguous`, listing the choices: the gateway never
picks a provider, and nothing falls back to another provider by itself, because providers can have different contracts and
failing can be better than an unexpected provider. To spread or fall back between providers, create a route under
*Routes* (it may carry the plain name; the route is then used) and order its targets. A key that allows the plain name
may use each provider's model of that name; restrict providers with the key's provider list.

**One Foundry resource, one provider.** On Foundry, OpenAI, DeepSeek, Grok, Llama, Mistral and other models are served
by the OpenAI v1 API, but Claude only by the Anthropic API. An *Azure AI Foundry* provider handles both: add the resource
once, with the project endpoint (`https://<resource>.services.ai.azure.com/api/projects/<project>`) and the API key
(`api-key` header). Discovery lists every deployment of the project, Claude included, and records the model behind each
one as its **base model** (`gpt-4.1` behind a deployment called `prod-chat`). The gateway sends a deployment whose base
model (or, without one, deployment name) starts with `claude` to the resource's `…/anthropic/v1` with the same key (in
the `x-api-key` header the Anthropic API reads), and
every other deployment to `…/openai/v1`. Health, circuit breaker, usage and a key's provider allow-list treat it as the
one provider. Clients can call `/v1/chat/completions` or `/v1/messages` for Claude either way: the gateway translates.
For a Claude deployment added by hand under a name of its own, set its *Base model* (`claude-sonnet-4-5`) in the model
drawer.

An *Anthropic* provider with the same project endpoint still works and lists only the Claude deployments, so providers
set up that way before need no change; new setups need only the Foundry provider.

A Foundry provider serves `/v1/responses` for GPT-5/GPT-6 and other models used through it. Reasoning models
(o-series, GPT-5 and later) are added with the reasoning parameter profile, which sends `max_completion_tokens` and drops
the sampling parameters they reject.

**Tool calls on gpt-5.6 and GPT-6 through Chat Completions.** These models cannot combine reasoning with function tools on
`/v1/chat/completions` (*"Function tools with reasoning_effort are not supported … use /v1/responses"*), and leaving
`reasoning_effort` out does not help: they reason by default. Some (gpt-6-astra, gpt-6.1-sol) also reject
`reasoning_effort: "none"`. The gateway therefore sends a Chat Completions request that has `tools` and does not set
`reasoning_effort: "none"` to the provider's `/v1/responses` instead, and turns the answer, or the stream, back into Chat
Completions form: clients and agents keep using Chat Completions and keep their reasoning. Nothing needs configuring. It
applies to models with the reasoning profile whose upstream name is gpt-5.6 or later or GPT-6 or later. A deployment with
a name of its own (`prod-chat`) is bridged the first time the provider answers with that error; the gateway resends the
request through the Responses API and remembers the deployment until it restarts. Requests without tools, and requests
with `reasoning_effort: "none"`, stay on Chat Completions. The bridged request is sent with `store: false` (Chat
Completions' default) and non-strict function schemas unless the client asked otherwise. `stop`, `logit_bias`, `logprobs`
and `seed` have no Responses equivalent and are left out; `n` above 1, audio output and audio input are refused with a
400 that says why.

**Suggested Foundry prices.** Azure's deployment list has no prices, so discovery looks the model up in Azure's public
retail price list (`https://prices.azure.com`, no key; the admin API needs outbound HTTPS to it) for the region in
`Admin:AzurePriceRegion` (default `swedencentral`, i.e. `Admin__AzurePriceRegion`). The list names meters in free text
(`gpt 4.1 cached Inp glbl Tokens`, `GPT 5 Mini Inpt DZone 1M Tokens`), so a price is suggested only when the meters for
the deployment's model, version and type (Global, Data Zone or Regional standard) agree on one input, output and, where
listed, cached-input and audio-token price. Provisioned and batch deployments, Claude (billed through Azure Marketplace)
and models the list names differently get no suggestion. A suggested price shows *Azure list price* and the meters it
came from; it is a list price, so check it against your agreement before you add the model. The list is cached for 12 hours.

### List prices

Model prices and details from models.dev. Prices for Anthropic (including Claude on
Foundry), Ollama Cloud, OpenAI and OpenRouter, and details and missing prices for Azure deployments, come from
[models.dev](https://models.dev), an open list maintained by its community (`Admin:PriceListUrl`, default `https://models.dev/api.json`, no key; the admin API needs outbound HTTPS to
`models.dev`). A provider's list is chosen by its type and address: `Anthropic` uses `anthropic` (at a Foundry address
`azure`), `AzureAIFoundry` and `AzureOpenAI` `azure`, `OllamaCloud` (or `Ollama` at `ollama.com`) `ollama-cloud`, an
OpenAI or OpenAI-compatible provider at `api.openai.com` `openai` and at `openrouter.ai` `openrouter`; the model is looked
up by its base model, else its upstream model name (Ollama's `:latest` and `-cloud` suffixes are tried without). Local
Ollama and other servers are not in the list.

**Azure deployments** are priced by region and deployment type (global, data zone, regional, provisioned), which the
list does not know, so their price is set by hand (*Set by hand* mode): discovery suggests Azure's own list price, else
the models.dev price, and the daily update gives the models.dev price only to a deployment that has **no price at all**,
and never changes it after that. Claude on Azure is billed at Anthropic's price, so a Claude deployment follows the list
like any other model.

Every model has a **price mode**, shown under its price in *Providers › Models* (and a *Price* filter):

| Mode | When | Who sets the price |
|---|---|---|
| *models.dev* | *Price from models.dev* is on for the model and its provider, and the list prices the model | the daily update; **Add price** is off (the API answers 409) |
| *Set by hand (not in models.dev)* | both are on, but the list does not have the model (or has no price for it) | an admin, until the list has it: then its list price takes over |
| *Set by hand* | *Price from models.dev* is off for the model or for its provider, or `Admin:AutoUpdatePrices` is off | an admin |

Both settings are on for new and existing models and providers. Turn one off for an agreed (discounted) price: on the
model (*Price from models.dev* in the model drawer) or for the whole provider (*Prices from models.dev* in the provider
drawer). Turning it on again lets the list price take over at the next update.

- **Daily update.** Once a day (two minutes after the admin API starts, then every 24 hours, or *Settings › Model prices ›
  Check now*) the admin API downloads the list and, for every model in mode *models.dev*, adds the list price to the
  price history when it differs from the current price. It applies from the time of the check; earlier usage keeps its
  price, and the gateways pick it up within seconds. Input, output, cached input, cache write and audio-token prices come
  from the list; what the list leaves out (audio duration, per-image price, a missing cache price) is kept. Context tiers
  (a higher price above 200k tokens) are not used: the base price applies. A list price of 0 for both input and output is
  treated as unknown and never applied. A price set for a future date is left until it applies.
- **Model details.** The same update fills in a listed model's empty **description**, **capabilities** (tools, thinking,
  vision, audio, image generation) and **context window**; it never replaces what is there. Discovery uses them too, for
  what the provider does not report. The description is shown in the catalogue key holders see.
- **Audit.** Each change is in the audit log as `system:price-list`, and the price history shows each price's source
  (`models.dev anthropic/claude-opus-4-5`, or *Admin*).
- **Turning it off.** `Admin:AutoUpdatePrices=false` (`Admin__AutoUpdatePrices`) stops the daily update (every model is
  then *Set by hand*) and keeps discovery's suggestions; an empty `Admin:PriceListUrl` stops both. Without internet
  access, serve a copy of `api.json` inside your network and set `Admin:PriceListUrl` to it. If the download fails, prices
  stay as they are and the next attempt is within 30 minutes; *Settings › Model prices* shows the error.

List prices are public prices: discounts in your agreement are not in them.

### Capabilities, context window and prompt caching

Capabilities are stored on each model (`features`), shown in the Models table, and can be
edited in the model drawer. Providers without a list price (local Ollama, vLLM and other servers) get their prices
under the model's price history. A model's *context window* is used for routing: a prompt clearly larger is
not sent to it (see [routing rules](routing-rules.md)).

**Prompt caching with Claude.** Clients of `/v1/messages` set their own `cache_control` markers. Requests the gateway
translates (OpenAI SDK clients calling a Claude model through `/v1/chat/completions`) get markers added, set by
`Gateway:PromptCaching`: `Static` (default) marks the tool definitions and the system prompt, which an application repeats
on every request; `Conversation` also marks the conversation so far, so the next turn of a chat reads it from the cache;
`Off` adds none. A cache hit costs a tenth of the input price, a write 1.25 times (enter the *Cache write* price below);
prompts below Anthropic's minimum (1 024 tokens, 2 048 for Haiku) are not cached and cost nothing extra.
`POST /v1/messages/count_tokens` is passed to the Anthropic provider and is free: no budget, no tokens counted.

*Cache write* is for providers that charge for writing the prompt cache:
Anthropic bills `cache_creation_input_tokens` at 1.25 times the input price (2 times for the 1-hour cache), so enter
that for Claude models; left empty, cache writes are billed at the input price (as before this field existed, which
undercharged them). Prices, costs and budgets are all in USD. Audio, image-generation-only and moderation models
are left out of the list. Models added before this feature have no capabilities until you set
them or re-add them through discovery. Database migration `ModelFeatures` adds the column and
runs with the normal migration service.
