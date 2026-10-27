# Speech to text

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

**Prices.** Whisper-style models are priced by the duration of the audio (*Audio input by duration (USD)* in the price
form, with the unit second, minute or hour; stored per minute as `audioPerMinuteUsd`). Such a model's audio tokens, if
the provider reports any, are not billed again as text. gpt-4o-transcribe per token, with audio tokens dearer than text tokens (*Audio input (USD per 1M
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
