export function snippets(baseUrl: string, model: string): { label: string; language: string; code: string }[] {
  const base = baseUrl.replace(/\/+$/, '') + '/v1'
  const quotedModel = JSON.stringify(model)
  return [
    { label: 'curl', language: 'bash', code: `curl ${JSON.stringify(base + '/chat/completions')} \\\n  -H "Authorization: Bearer $UME_API_KEY" \\\n  -H "Content-Type: application/json" \\\n  -d '${JSON.stringify({ model, messages: [{ role: 'user', content: 'Hej!' }] }).replace(/'/g, "'\\''")}'` },
    { label: '.NET OpenAI SDK', language: 'csharp', code: `using OpenAI;\nusing System.ClientModel;\n\nvar client = new OpenAIClient(\n    new ApiKeyCredential(Environment.GetEnvironmentVariable("UME_API_KEY")!),\n    new OpenAIClientOptions { Endpoint = new Uri(${JSON.stringify(base)}) });\nvar chat = client.GetChatClient(${quotedModel});\nvar result = await chat.CompleteChatAsync("Hej!");\n// Do not log or persist the response.` },
    { label: 'Python OpenAI SDK', language: 'python', code: `import os\nfrom openai import OpenAI\n\nclient = OpenAI(api_key=os.environ["UME_API_KEY"], base_url=${JSON.stringify(base)})\nresult = client.chat.completions.create(\n    model=${quotedModel},\n    messages=[{"role": "user", "content": "Hej!"}])\n# Do not log or persist the response.` },
    { label: 'JavaScript / TypeScript OpenAI SDK', language: 'typescript', code: `import OpenAI from 'openai';\n\nconst client = new OpenAI({ apiKey: process.env.UME_API_KEY, baseURL: ${JSON.stringify(base)} });\nconst result = await client.chat.completions.create({\n  model: ${quotedModel},\n  messages: [{ role: 'user', content: 'Hej!' }],\n});\n// Do not log or persist the response.` },
    { label: 'Anthropic SDK (Anthropic model required)', language: 'python', code: `import os\nfrom anthropic import Anthropic\n\nclient = Anthropic(api_key=os.environ["UME_API_KEY"], base_url=${JSON.stringify(baseUrl.replace(/\/+$/, ''))})\nresult = client.messages.create(\n    model=${quotedModel}, max_tokens=100,\n    messages=[{"role": "user", "content": "Hej!"}])\n# Select an alias with AnthropicMessages capability. Never log the response.` },
  ]
}
