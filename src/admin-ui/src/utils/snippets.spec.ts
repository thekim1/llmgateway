import { describe, expect, it } from 'vitest'
import { snippets } from './snippets'

describe('snippets', () => {
  it('shows chat examples for chat aliases', () => {
    const [curl] = snippets('https://gw.example/', 'ume/chat-standard')
    expect(curl?.code).toContain('https://gw.example/v1/chat/completions')
  })

  it('shows file uploads to the transcription endpoint for speech-to-text aliases', () => {
    const examples = snippets('https://gw.example', 'ume/transcribe', 'Transcription')
    expect(examples.map((e) => e.label)).not.toContain('Anthropic SDK (Anthropic model required)')
    expect(examples[0]?.code).toContain('https://gw.example/v1/audio/transcriptions')
    expect(examples[0]?.code).toContain('-F file=@samtal.mp3')
    expect(examples.every((e) => e.code.includes('"ume/transcribe"') || e.code.includes("'ume/transcribe'"))).toBe(true)
  })

  it('adds a live transcription example over WebSocket for speech-to-text aliases', () => {
    const live = snippets('https://gw.example', 'ume/transcribe', 'Transcription').find((e) => e.label.startsWith('Live transcription'))
    expect(live?.code).toContain('wss://gw.example/v1/realtime?model=ume%2Ftranscribe')
    expect(live?.code).toContain('input_audio_buffer.append')
  })

  it('shows WebSocket sessions for realtime and interpreting aliases', () => {
    const realtime = snippets('https://gw.example', 'ume/realtime', 'Realtime')
    expect(realtime.map((e) => e.label)).toEqual([
      'Realtime session (Python, websockets)',
      'Realtime session (.NET, ClientWebSocket)',
      'Realtime session (Node.js, ws)',
    ])
    expect(realtime[0]?.code).toContain('wss://gw.example/v1/realtime?model=ume%2Frealtime')

    const interpreting = snippets('http://localhost:5140/', 'ume/interpret', 'SpeechTranslation')
    expect(interpreting[0]?.code).toContain('ws://localhost:5140/v1/realtime/translations?model=ume%2Finterpret')
    expect(interpreting[0]?.code).toContain('session.input_audio_buffer.append')
    expect(interpreting[2]?.code).toContain('session.output_transcript.delta')
  })
})
