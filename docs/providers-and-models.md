# Providers and models

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
| Anthropic | `GET {baseUrl}/models`; Claude on Foundry (project endpoint as base URL) lists its Claude deployments from the project API | context size and capabilities |
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

**One Foundry resource, two providers.** The gateway speaks a provider's protocol by its *type*, not by the model, and
translates between them: clients can call `/v1/chat/completions` for any model, including Claude. On Foundry, OpenAI,
DeepSeek, Grok, Llama, Mistral and other models are served by the OpenAI v1 API, but Claude only by the Anthropic API. So
add the resource twice with the same key:

| Provider type | Base URL | Authentication | Discovery lists |
|---|---|---|---|
| Azure AI Foundry | the project endpoint, `https://<resource>.services.ai.azure.com/api/projects/<project>` (requests go to `…/openai/v1`) | API key header (`api-key`) | every deployment except Claude |
| Anthropic | the same project endpoint (requests go to `…/anthropic/v1`) | API key header (`api-key`) | the Claude deployments |

Tick *Responses* on the Foundry provider for GPT-5/GPT-6 and other models used through `/v1/responses`. Reasoning models
(o-series, GPT-5 and later) are added with the reasoning parameter profile, which sends `max_completion_tokens` and drops
the sampling parameters they reject.

**Suggested Foundry prices.** Azure's deployment list has no prices, so discovery looks the model up in Azure's public
retail price list (`https://prices.azure.com`, no key; the admin API needs outbound HTTPS to it) for the region in
`Admin:AzurePriceRegion` (default `swedencentral`, i.e. `Admin__AzurePriceRegion`). The list names meters in free text
(`gpt 4.1 cached Inp glbl Tokens`, `GPT 5 Mini Inpt DZone 1M Tokens`), so a price is suggested only when the meters for
the deployment's model, version and type (Global, Data Zone or Regional standard) agree on one input, output and, where
listed, cached-input and audio-token price. Provisioned and batch deployments, Claude (billed through Azure Marketplace)
and models the list names differently get no suggestion. A suggested price shows *Azure list price* and the meters it
came from; it is a list price, so check it against your agreement before you add the model. The list is cached for 12 hours.

Capabilities are stored on each model (`features`), shown in the Models table, and can be
edited in the model drawer. Most providers, including Ollama Cloud, do not publish prices, so
add them under the model's price history. A model's *context window* is used for routing: a prompt clearly larger is
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
undercharged them). Prices are in USD, so also enter the USD exchange rate under **Settings**:
until one is in effect the gateway uses `Gateway:FallbackSekPerUsd` (10 SEK), logs a warning and reports its readiness
check `exchange-rate` as Degraded (visible under operations health). Audio, image-generation-only and moderation models
are left out of the list. Models added before this feature have no capabilities until you set
them or re-add them through discovery. Database migration `ModelFeatures` adds the column and
runs with the normal migration service.
