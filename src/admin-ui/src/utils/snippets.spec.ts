import { describe, expect, it } from 'vitest'
import { mcpSnippets, snippets } from './snippets'

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

describe('mcpSnippets', () => {
  it('configures Claude Code, VS Code and Claude Desktop for the aggregated /mcp', () => {
    const [code, vscode, desktop] = mcpSnippets('https://gw.example/')
    expect(code?.code).toBe('claude mcp add --transport http ume https://gw.example/mcp --header "Authorization: Bearer $UME_API_KEY"')
    expect(JSON.parse(vscode!.code).servers.ume).toEqual({ type: 'http', url: 'https://gw.example/mcp', headers: { Authorization: 'Bearer ${input:ume-key}' } })
    expect(JSON.parse(desktop!.code).mcpServers.ume.args).toEqual(['-y', 'mcp-remote', 'https://gw.example/mcp', '--header', 'Authorization:${UME_AUTH}'])
  })

  it('points at one server with its own tool names when a slug is given', () => {
    expect(mcpSnippets('https://gw.example', 'case_files')[0]?.code).toContain('ume-case-files https://gw.example/mcp/case_files')
  })
})
