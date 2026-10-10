import type { ModelKind } from '@/api/types'

export function snippets(baseUrl: string, model: string, kind: ModelKind = 'Chat'): { label: string; language: string; code: string }[] {
  const base = baseUrl.replace(/\/+$/, '') + '/v1'
  const quotedModel = JSON.stringify(model)
  if (kind === 'Transcription') return [...transcriptionSnippets(base, model), ...liveSnippets(base, model, kind).slice(0, 1)]
  if (kind === 'Realtime' || kind === 'SpeechTranslation') return liveSnippets(base, model, kind)
  return [
    { label: 'curl', language: 'bash', code: `curl ${JSON.stringify(base + '/chat/completions')} \\\n  -H "Authorization: Bearer $UME_API_KEY" \\\n  -H "Content-Type: application/json" \\\n  -d '${JSON.stringify({ model, messages: [{ role: 'user', content: 'Hej!' }] }).replace(/'/g, "'\\''")}'` },
    { label: '.NET OpenAI SDK', language: 'csharp', code: `using OpenAI;\nusing System.ClientModel;\n\nvar client = new OpenAIClient(\n    new ApiKeyCredential(Environment.GetEnvironmentVariable("UME_API_KEY")!),\n    new OpenAIClientOptions { Endpoint = new Uri(${JSON.stringify(base)}) });\nvar chat = client.GetChatClient(${quotedModel});\nvar result = await chat.CompleteChatAsync("Hej!");\n// Do not log or persist the response.` },
    { label: 'Python OpenAI SDK', language: 'python', code: `import os\nfrom openai import OpenAI\n\nclient = OpenAI(api_key=os.environ["UME_API_KEY"], base_url=${JSON.stringify(base)})\nresult = client.chat.completions.create(\n    model=${quotedModel},\n    messages=[{"role": "user", "content": "Hej!"}])\n# Do not log or persist the response.` },
    { label: 'JavaScript / TypeScript OpenAI SDK', language: 'typescript', code: `import OpenAI from 'openai';\n\nconst client = new OpenAI({ apiKey: process.env.UME_API_KEY, baseURL: ${JSON.stringify(base)} });\nconst result = await client.chat.completions.create({\n  model: ${quotedModel},\n  messages: [{ role: 'user', content: 'Hej!' }],\n});\n// Do not log or persist the response.` },
    { label: 'Anthropic SDK (Anthropic model required)', language: 'python', code: `import os\nfrom anthropic import Anthropic\n\nclient = Anthropic(api_key=os.environ["UME_API_KEY"], base_url=${JSON.stringify(baseUrl.replace(/\/+$/, ''))})\nresult = client.messages.create(\n    model=${quotedModel}, max_tokens=100,\n    messages=[{"role": "user", "content": "Hej!"}])\n# Select an alias with AnthropicMessages capability. Never log the response.` },
  ]
}

/** Speech to text: a multipart upload to /v1/audio/transcriptions (use /v1/audio/translations for English output). */
function transcriptionSnippets(base: string, model: string): { label: string; language: string; code: string }[] {
  const quotedModel = JSON.stringify(model)
  return [
    { label: 'curl', language: 'bash', code: `curl ${JSON.stringify(base + '/audio/transcriptions')} \\\n  -H "Authorization: Bearer $UME_API_KEY" \\\n  -F model=${quotedModel} \\\n  -F language=sv \\\n  -F file=@samtal.mp3` },
    { label: '.NET OpenAI SDK', language: 'csharp', code: `using OpenAI;\nusing OpenAI.Audio;\nusing System.ClientModel;\n\nvar client = new OpenAIClient(\n    new ApiKeyCredential(Environment.GetEnvironmentVariable("UME_API_KEY")!),\n    new OpenAIClientOptions { Endpoint = new Uri(${JSON.stringify(base)}) });\nvar audio = client.GetAudioClient(${quotedModel});\nvar result = await audio.TranscribeAudioAsync("samtal.mp3", new AudioTranscriptionOptions { Language = "sv" });\n// result.Value.Text is the transcript. Do not log or persist it.` },
    { label: 'Python OpenAI SDK', language: 'python', code: `import os\nfrom openai import OpenAI\n\nclient = OpenAI(api_key=os.environ["UME_API_KEY"], base_url=${JSON.stringify(base)})\nwith open("samtal.mp3", "rb") as audio:\n    result = client.audio.transcriptions.create(model=${quotedModel}, file=audio, language="sv")\n# result.text is the transcript. Do not log or persist it.` },
    { label: 'JavaScript / TypeScript OpenAI SDK', language: 'typescript', code: `import fs from 'node:fs';\nimport OpenAI from 'openai';\n\nconst client = new OpenAI({ apiKey: process.env.UME_API_KEY, baseURL: ${JSON.stringify(base)} });\nconst result = await client.audio.transcriptions.create({\n  model: ${quotedModel},\n  file: fs.createReadStream('samtal.mp3'),\n  language: 'sv',\n});\n// result.text is the transcript. Do not log or persist it.` },
  ]
}

/**
 * Live audio over WebSocket (OpenAI Realtime protocol): live transcription and realtime sessions on /v1/realtime,
 * interpreting on /v1/realtime/translations. Audio is 24 kHz 16-bit mono PCM, base64 in ~100 ms chunks.
 */
function liveSnippets(base: string, model: string, kind: ModelKind): { label: string; language: string; code: string }[] {
  const translate = kind === 'SpeechTranslation'
  const url = base.replace(/^http/, 'ws') + (translate ? '/realtime/translations' : '/realtime') + '?model=' + encodeURIComponent(model)
  const append = translate ? 'session.input_audio_buffer.append' : 'input_audio_buffer.append'
  const session =
    kind === 'Transcription'
      ? { type: 'transcription', audio: { input: { format: { type: 'audio/pcm', rate: 24000 }, transcription: { model, language: 'sv' } } } }
      : translate
        ? { audio: { output: { language: 'en' } } }
        : { type: 'realtime', output_modalities: ['text'], instructions: 'Svara kort på svenska.' }
  const result =
    kind === 'Transcription' ? 'conversation.item.input_audio_transcription.completed' : translate ? 'session.output_transcript.delta' : 'response.output_text.delta'
  const field = kind === 'Transcription' ? 'transcript' : 'delta'
  const label = kind === 'Transcription' ? 'Live transcription' : translate ? 'Live interpreting' : 'Realtime session'
  return [
    {
      label: `${label} (Python, websockets)`,
      language: 'python',
      code: `import asyncio, base64, json, os\nimport websockets\n\nasync def main(pcm_chunks):\n    headers = {"Authorization": f"Bearer {os.environ['UME_API_KEY']}"}\n    async with websockets.connect(${JSON.stringify(url)}, additional_headers=headers) as ws:\n        await ws.send(json.dumps({"type": "session.update", "session": ${JSON.stringify(session)}}))\n        async def send_audio():\n            for chunk in pcm_chunks:  # 24 kHz 16-bit mono PCM, about 100 ms (4 800 bytes) each\n                await ws.send(json.dumps({"type": "${append}", "audio": base64.b64encode(chunk).decode()}))\n        sender = asyncio.create_task(send_audio())\n        async for message in ws:\n            event = json.loads(message)\n            if event["type"] == "${result}":\n                handle(event["${field}"])  # do not log or persist it\n            elif event["type"] == "error":\n                raise RuntimeError(event["error"]["message"])`,
    },
    {
      label: `${label} (.NET, ClientWebSocket)`,
      language: 'csharp',
      code: `using System.Net.WebSockets;\nusing System.Text;\nusing System.Text.Json;\n\nusing var ws = new ClientWebSocket();\nws.Options.SetRequestHeader("Authorization", "Bearer " + Environment.GetEnvironmentVariable("UME_API_KEY"));\nawait ws.ConnectAsync(new Uri(${JSON.stringify(url)}), ct);\nawait SendAsync(new { type = "session.update", session = JsonSerializer.Deserialize<JsonElement>(${JSON.stringify(JSON.stringify(session))}) });\n// For each ~100 ms of 24 kHz 16-bit mono PCM:\n// await SendAsync(new { type = "${append}", audio = Convert.ToBase64String(chunk) });\n// Read events with ws.ReceiveAsync until EndOfMessage; "${result}" carries the text in "${field}".\n\nTask SendAsync(object evt) =>\n    ws.SendAsync(JsonSerializer.SerializeToUtf8Bytes(evt), WebSocketMessageType.Text, true, ct);`,
    },
    {
      label: `${label} (Node.js, ws)`,
      language: 'typescript',
      code: `import WebSocket from 'ws';\n\nconst ws = new WebSocket(${JSON.stringify(url)}, {\n  headers: { Authorization: \`Bearer \${process.env.UME_API_KEY}\` },\n});\nws.on('open', () => {\n  ws.send(JSON.stringify({ type: 'session.update', session: ${JSON.stringify(session)} }));\n  // For each ~100 ms of 24 kHz 16-bit mono PCM (a Buffer):\n  // ws.send(JSON.stringify({ type: '${append}', audio: chunk.toString('base64') }));\n});\nws.on('message', (data) => {\n  const event = JSON.parse(data.toString());\n  if (event.type === '${result}') handle(event.${field}); // do not log or persist it\n});`,
    },
  ]
}
