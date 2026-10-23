# Content guard

The content guard checks what a key's applications send (and, if you want, what the models answer) for prompt injection
and harmful content, **without sending anything outside your network**. It is off for every key until an administrator
turns it on, and keys with it off pay nothing for it.

It has two layers:

| Layer | What it catches | Where it runs | Cost per checked request |
|---|---|---|---|
| **Rules** | Well-known prompt-injection phrasings in English and Swedish ("ignore all previous instructions", "visa din systemprompt", "you are now DAN"…) and your own blocked patterns | Inside the gateway | Microseconds (a few regular expressions over the text, like the personal-data scan) |
| **Guard model** (optional) | Harmful content in the categories the model was trained on (violence, hate, self-harm, sexual content, crime, weapons, privacy, prompt attacks…) | A small safety model on **your own** on-prem inference server, called by the gateway | One extra model call: about 50–300 ms on a GPU, around a second on CPU |

The rules are a first line of defence: cheap and predictable, but a determined user rewords. The guard model understands
meaning, so it catches what rules cannot, at the price of latency and some GPU capacity.

## Per key: Off, Record, Block

*Keys > edit > Content guard* (admin API `contentGuardPolicy`):

- **Off** (default): no checks.
- **Record**: the request is checked and sent on unchanged. What the guard found is recorded on the usage record
  (`GuardFlags`, shown as *Content guard* on the request in *Usage*), the response gets `x-ume-guard: flagged`, and the
  security event `gateway.content.flagged` is logged. Use it first, to see what the guard would catch before refusing
  anything.
- **Block**: a flagged request is refused with `400 content_blocked` before any model is called (and costs nothing); a
  flagged answer is withheld with `502 content_blocked` (the provider has answered, so its cost is recorded).

Only categories are stored (`request:prompt_injection`, `request:S1`, `answer:blocked_pattern`), never the text. The guard
runs after the personal-data policy, so with *Redact* it reads the redacted text.

## Where the guard model runs

The gateway does not host models. The guard model runs on the same kind of on-prem inference server you use for your
other on-prem models (vLLM or Ollama), and you add it to the gateway like any model: a provider with residency
**On-prem**, a model deployment, a name. The gateway calls it with the text to check and reads the verdict.

**The gateway only uses a guard model on an on-prem provider.** If `Gateway:ContentGuard:Model` names a model whose
provider is EU or External, the gateway logs a warning and uses the rules only: the text to check never leaves your
network, whatever is configured.

Models that work (the answer format is set with `Gateway:ContentGuard:Format`):

| Model | Size | Licence | Format | Notes |
|---|---|---|---|---|
| **IBM Granite Guardian 3.x** | 2B, 8B | Apache 2.0 | `GraniteGuardian` (Yes/No) | Permissive licence; good default for a public organisation |
| **Meta Llama Guard 3** | 1B, 8B | Llama 3 Community Licence | `LlamaGuard` (safe / unsafe + categories S1–S14) | Names the category (`S1` violent crimes … `S14` code interpreter abuse) |

The 1B and 2B models run on a modest GPU or, slowly, on CPU; the 8B models want a GPU with about 16 GB of memory. The
guard only reads the text and writes a few words, so it is far cheaper than a chat model of the same size.

### Setting it up (example: Llama Guard 3 1B on Ollama)

1. On the on-prem server: `ollama pull llama-guard3:1b` (with vLLM: serve `meta-llama/Llama-Guard-3-1B`, or
   `ibm-granite/granite-guardian-3.1-2b` for Granite Guardian).
2. In the admin UI, *Providers*: the on-prem Ollama provider (residency **On-prem**) already exists, or add it. Add the
   model `llama-guard3:1b` (kind Chat). Leave its price at 0; do not give keys access to it, they never call it.
3. On the gateway: `Gateway__ContentGuard__Model=<provider>/llama-guard3:1b` and
   `Gateway__ContentGuard__Format=LlamaGuard`, then restart the gateway.
4. Set a test key to *Record*, send a few requests, and look at *Usage* (*Content guard*) and the gateway log.
5. Move keys to *Block* once you trust what it flags.

Without step 3 the guard uses the rules only.

## Performance

- **Keys with the guard off:** one comparison per request; nothing else runs.
- **Rules:** microseconds, in the gateway.
- **Guard model, per checked request:** one call before the request goes on (so the time to the first word grows by the
  guard's answer time, typically 50–300 ms on a GPU), and with `CheckAnswers` one more call after the answer.
  Streamed answers are not checked (the client already has them).
- **Capacity:** the guard server must keep up with the request rate of the keys that use it. A 1B/2B guard on one GPU
  handles many requests per second; size it like any on-prem model.
- **When the guard model is down or slow** (`TimeoutMs`, 5 s): with `FailOpen` (default) requests go on and are recorded
  as `guard_unavailable`; with `FailOpen=false`, keys with *Block* are refused with `503 content_guard_unavailable` until
  it is back. Keys with *Record* always go on.

The guard model's own calls are not usage records and do not count against budgets (an on-prem model costs nothing).

## Settings (`Gateway:ContentGuard:*`)

| Setting | Default | |
|---|---|---|
| `Model` | (empty) | The guard model's name in the catalogue (`provider/name` or a route). Empty: rules only |
| `Format` | `LlamaGuard` | `LlamaGuard` or `GraniteGuardian` |
| `CheckAnswers` | `false` | Also check complete (non-streamed) answers |
| `TimeoutMs` | `5000` | Longest wait for the guard model |
| `FailOpen` | `true` | Let requests through (and record it) when the guard model is unavailable |
| `PromptInjectionRules` | `true` | The built-in prompt-injection rules |
| `BlockedPatterns` | (none) | Your own regular expressions, case-insensitive (`Gateway__ContentGuard__BlockedPatterns__0=...`) |
| `MaxCharacters` | `16000` | How much text is checked: the last part of a long conversation (the most recent turns) |

## Limits

- No guard is perfect: it misses some harmful content and flags some harmless content. Start with *Record* and look at
  what it flags before you block.
- The rules and the guard model read text. Images, audio and files are not checked (use the attached-files policy).
- Streamed answers, live audio and token counting are not checked.
