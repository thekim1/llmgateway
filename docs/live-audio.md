# Live audio (realtime)

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
  prices when set, duration at its duration price (entered per second, minute or hour). A model with a duration price
  and no audio token price bills its audio input by duration only. Interpreting is billed by the counted duration.
- **Budgets.** Five minutes of audio are reserved at connect and again whenever the reservation runs low; when a budget
  runs out the client gets an `error` event (`budget_exceeded`) and the session is closed.
- **PII.** Text in `session.update`, `conversation.item.create` and `response.create` goes through the key's PII
  policy: blocked or redacted as for chat, and refused (not rerouted) when it may only go on-prem but the session runs
  elsewhere. Audio itself cannot be scanned; choose on-prem or EU aliases for conversations with personal data.
- **Limits.** 20 open sessions per key across instances, 120 minutes per session, 4 MB per event
  (`Gateway__Realtime__*`, see [admin-api.md](admin-api.md#live-audio-realtime)). Binary frames are refused.

A session is one usage record (endpoint `Realtime` or `RealtimeTranslations`): `latencyMs` is the session length and
`audioSeconds` the audio streamed. Audio is never stored or logged.

**Providers.** OpenAI (`https://api.openai.com/v1`), Azure OpenAI and Azure AI Foundry (`https://<resource>.openai.azure.com/openai/v1`
or `…services.ai.azure.com/openai/v1`), Foundry Voice Live for other models in Sweden
(`https://<resource>.services.ai.azure.com/voice-live?api-version=2026-04-10`; base URLs may now carry query parameters,
never credentials), and on-prem servers with the same protocol such as vLLM and Speaches (`http://<server>/v1`). An
OpenAI-compatible server needs *Live audio (realtime)* under *Endpoints this server offers*; the other types serve it
already. Behind nginx, pass the WebSocket upgrade (see
`deploy/proxy/nginx.conf.example`).
