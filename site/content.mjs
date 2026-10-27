// Everything the website says that is not a Markdown page. The documentation pages themselves are the Markdown files
// listed in DOCS; the build renders them, so the repository and the website never disagree.

export const GH = 'https://github.com/thekim1/llmgateway'

/**
 * Documentation sidebar, grouped by what the reader came to do: try or install it, call it from an application, set it
 * up, run it, check it, measure it, or look something up. `source` is relative to the repository root; `slug` is the
 * page's URL under /docs/.
 */
export const DOCS = [
  { group: 'Get started', items: [
    { slug: '', title: 'Introduction', source: 'docs/introduction.md', lead: 'What the gateway does, what it stores and what it does not.' },
    { slug: 'local-development', title: 'Local development', source: 'docs/local-development.md', lead: 'Run the full stack on Windows with Aspire, demo data and a fake LLM, and run the tests.' },
    { slug: 'production-setup', title: 'Production setup', source: 'docs/production-setup.md', lead: 'Deploy a release bundle with Docker Compose on a Linux host, behind your own login.' },
    { slug: 'kubernetes', title: 'Kubernetes', source: 'docs/kubernetes.md', lead: 'Kustomize manifests with rolling updates, optional in-cluster Postgres and Redis, and the restricted Pod Security Standard.' },
    { slug: 'identity-and-roles', title: 'Identity provider and roles', source: 'docs/identity-and-roles.md', lead: 'Sign in with Keycloak, Entra ID or AD FS, and what gateway admins, department admins and viewers may do.' },
  ] },
  { group: 'Build with the API', items: [
    { slug: 'attached-files', title: 'Attached files', source: 'docs/attached-files.md', lead: 'Images, PDFs, audio and video inline in the request body, and the per-key policy that limits them.' },
    { slug: 'speech-to-text', title: 'Speech to text', source: 'docs/speech-to-text.md', lead: 'Transcription and translation with Whisper-style models.' },
    { slug: 'image-generation', title: 'Image generation', source: 'docs/image-generation.md', lead: 'Generate and edit pictures with gpt-image-1, DALL·E, FLUX and other OpenAI-compatible models.' },
    { slug: 'live-audio', title: 'Live audio', source: 'docs/live-audio.md', lead: 'Realtime sessions, live transcription and interpreting over WebSockets.' },
  ] },
  { group: 'Set up the gateway', items: [
    { slug: 'admin-ui', title: 'Admin UI', source: 'docs/admin-ui.md', lead: 'Find your way around the menu and search, then provider status, the usage dashboard and routing rules.' },
    { slug: 'providers-and-models', title: 'Providers and models', source: 'docs/providers-and-models.md', lead: 'Discover models from a provider, the same model from several providers, and suggested Azure prices.' },
    { slug: 'routing-rules', title: 'Routing rules', source: 'docs/routing-rules.md', lead: 'Send requests to the right model based on headers, team, budget use or personal data, with weights and fallbacks.' },
    { slug: 'personal-data', title: 'Personal data (PII)', source: 'docs/personal-data.md', lead: 'Detect personnummer, email addresses, phone numbers and IBANs, then record, redact, block or keep the request on-prem.' },
    { slug: 'content-guard', title: 'Content guard', source: 'docs/content-guard.md', lead: 'Check requests and answers for prompt injection and harmful content with rules and an on-prem guard model.' },
    { slug: 'client-addresses', title: 'Client addresses', source: 'docs/client-addresses.md', lead: 'Limit a key to the addresses it may be used from, also behind a reverse proxy.' },
  ] },
  { group: 'Run it', items: [
    { slug: 'runbook', title: 'Runbook', source: 'docs/runbook.md', lead: 'Deployment, identity provider, certificates, backups and incidents.' },
    { slug: 'upgrade-guide', title: 'Upgrade guide', source: 'docs/upgrade-guide.md', lead: 'Migrations, rollback and certificates, per release.' },
    { slug: 'zero-downtime-updates', title: 'Updates without downtime', source: 'docs/zero-downtime-updates.md', lead: 'Two instances behind a load balancer, replaced one at a time: opt-in for Docker Compose, built in on Kubernetes.' },
    { slug: 'data-retention', title: 'Data retention and logging', source: 'docs/data-retention.md', lead: 'How long request data is kept, and when successful requests are logged.' },
    { slug: 'security-events', title: 'Security events', source: 'docs/security-events.md', lead: 'Send security events to your SIEM with OpenTelemetry, and what the Data API offers.' },
    { slug: 'data-access', title: 'Data API', source: 'docs/data-access.md', lead: 'Read-only access to the gateway\'s data for BI, management and security teams.' },
  ] },
  { group: 'Security and compliance', items: [
    { slug: 'security-and-compliance', title: 'Security and compliance', source: 'docs/security-and-compliance.md', lead: 'Controls, DPIA and processing-record inputs, release gates.' },
    { slug: 'accessibility-statement', title: 'Accessibility statement', source: 'docs/accessibility-statement.md', lead: 'Automated evidence and outstanding manual checks (in Swedish).' },
  ] },
  { group: 'Performance', items: [
    { slug: 'performance', title: 'Performance', source: 'docs/performance.md', lead: 'What a request costs, every benchmark result and how to measure it.' },
  ] },
  { group: 'Reference', items: [
    { slug: 'admin-api', title: 'Admin API', source: 'docs/admin-api.md', lead: 'The contract between the admin UI and the control plane, and the gateway\'s data plane.' },
    { slug: 'architecture', title: 'Architecture', source: 'docs/architecture.md', lead: 'Components, trust boundaries and tradeoffs.' },
    { slug: 'adr', title: 'Design decisions', source: 'docs/adr.md', lead: 'Architecture decision records.' },
    { slug: 'changelog', title: 'Changelog', source: 'CHANGELOG.md', lead: 'Changes per release.' },
  ] },
]

/** Folders copied as they are, so Markdown can link to them (images, the platform comparison report). */
export const DOC_ASSETS = ['docs/images', 'docs/reports']

export const SDK = [
  { id: 'python', label: 'Python', code: `import os
from openai import OpenAI

client = OpenAI(
    base_url="https://gateway.example.se/v1",
    api_key=os.environ["UME_API_KEY"],   # a virtual key
)

reply = client.chat.completions.create(
    model="ume/chat-standard",           # an alias, not a vendor model
    messages=[{"role": "user", "content": "Sammanfatta ärendet"}],
)` },
  { id: 'node', label: 'Node.js', code: `import OpenAI from "openai";

const client = new OpenAI({
  baseURL: "https://gateway.example.se/v1",
  apiKey: process.env.UME_API_KEY,
});

const reply = await client.chat.completions.create({
  model: "ume/chat-standard",
  messages: [{ role: "user", content: "Sammanfatta ärendet" }],
});` },
  { id: 'dotnet', label: '.NET', code: `using OpenAI;
using System.ClientModel;

var client = new OpenAIClient(
    new ApiKeyCredential(Environment.GetEnvironmentVariable("UME_API_KEY")!),
    new OpenAIClientOptions { Endpoint = new Uri("https://gateway.example.se/v1") });

var chat = client.GetChatClient("ume/chat-standard");
var reply = await chat.CompleteChatAsync("Sammanfatta ärendet");` },
  { id: 'curl', label: 'curl', code: `curl https://gateway.example.se/v1/chat/completions \\
  -H "Authorization: Bearer $UME_API_KEY" \\
  -H "Content-Type: application/json" \\
  -d '{"model":"ume/chat-standard",
       "messages":[{"role":"user","content":"Hej"}]}'

# x-ume-rule, x-ume-residency, x-ume-fallbacks
# come back as response headers` },
]

export const ENDPOINTS = ['/v1/chat/completions', '/v1/responses', '/v1/messages', '/v1/embeddings', '/v1/audio/transcriptions', '/v1/images/generations', '/v1/realtime', '/v1/models']

export const PROOF = [
  { value: '1.0 ms', label: 'Added latency', sub: 'Median overhead per chat request, one client' },
  { value: '17 888', label: 'Messages per second', sub: 'Streaming chat on 4 vCPUs' },
  { value: '0', label: 'Database calls per request', sub: 'Keys and routing are cached in memory' },
  { value: '0', label: 'Prompts stored', sub: 'Usage metadata only, by design' },
]

export const PILLARS = [
  { icon: 'shield_lock', tone: 'ok', title: 'Personal data stays where it should', body: 'Detect Swedish personnummer, samordningsnummer, emails, phone numbers and IBANs, then block, redact or keep the request on-prem.', points: ['Per-key PII policy', 'Residency limits: on-prem, EU, external', 'Prompts and responses never stored'] },
  { icon: 'savings', tone: 'accent', title: 'Budgets, not surprises', body: 'Set limits per department, team and key. Alerts fire at your thresholds and requests stop when a budget runs out.', points: ['Monthly, weekly or quarterly periods', 'Limits in USD, like provider prices', 'Exports for finance and BI'] },
  { icon: 'electric_bolt', tone: 'warn', title: 'Keeps working when a provider doesn\'t', body: 'Ordered fallback within the allowed residencies. A circuit breaker moves a failing provider to the back of the line.', points: ['Retries only what is safe to retry', 'Never falls back mid-stream', 'Health visible in the admin UI'] },
]

export const UI_SHOTS = [
  { file: 'keys-drawer', alt: 'The Keys page with a key\'s details open in a drawer', title: 'Keys', body: 'Create, rotate with a grace period, or revoke. Secrets are shown once.' },
  { file: 'budgets', alt: 'The Budgets and alerts page with budgets per department and team', title: 'Budgets & alerts', body: 'See who is close to their limit before requests start failing.' },
  { file: 'usage', alt: 'The usage dashboard with cost, requests, latency and time to first token', title: 'Usage', body: 'Latency, time to first token and cost per model, department and rule.' },
]

export const DEPLOYS = [
  { icon: 'deployed_code', title: 'Docker Compose', body: 'Hardened production file: read-only, non-root, internal data network. One script creates certificates and secrets.', meta: 'deploy/compose.prod.yaml', href: 'docs/production-setup/' },
  { icon: 'web', title: 'Portainer', body: 'No shell access? The same stack, configured only with environment variables. Certificates are generated on first start.', meta: 'compose.portainer.yaml', href: 'docs/runbook/#portainer-and-other-hosts-without-shell-access' },
  { icon: 'badge', title: 'Your identity provider', body: 'Keycloak, AD FS or Entra ID. Roles, department codes and AD groups are mapped from claims.', meta: 'UME_OIDC_AUTHORITY', href: 'docs/runbook/#identity-provider-existing-keycloak-ad-fs-entra-id-active-directory' },
]

export const PATHS = [
  { id: 'local', title: 'Try it locally', body: 'Windows, Docker and Aspire. Demo data and a fake LLM.', icon: 'laptop_mac',
    prereqs: ['.NET SDK 10.0.401', 'Aspire 13.6', 'Docker 28+', 'Node 24.15+'],
    steps: [
      { title: 'Install the admin UI packages', body: 'Clone the repository, then install the UI dependencies once.', shell: 'PowerShell', code: `git clone ${GH}.git
Set-Location llmgateway\\src\\admin-ui
npm ci
Set-Location ..\\..` },
      { title: 'Start the stack', body: 'Aspire starts Postgres, Redis, Keycloak, the fake LLM, the gateway and the admin UI. Demo data is on by default.', shell: 'PowerShell', code: `.\\scripts\\Start-Dev.ps1          # with demo data
.\\scripts\\Start-Dev.ps1 -Empty   # empty database, only login users`, note: 'Prefer Visual Studio? Open UmeLlmGateway.slnx, set Ume.LlmGateway.AppHost as the startup project and press F5.' },
      { title: 'Sign in and create a key', body: 'Open https://localhost:5173 and sign in as the gateway-admin test user (the password is the dev-user-password secret in the AppHost user secrets). Under Keys, choose New key. The secret is shown once, so copy it now.', shot: 'secret-dialog', shotAlt: 'The “Copy your secret now” dialog after creating a key' },
      { title: 'Send your first request', body: 'Use the key with any OpenAI-compatible client. The fake LLM answers instantly.', shell: 'bash', code: `curl https://localhost:7212/v1/chat/completions \\
  -H "Authorization: Bearer $UME_API_KEY" \\
  -H "Content-Type: application/json" \\
  -d '{"model":"ume/chat-standard","messages":[{"role":"user","content":"Hej"}]}'` },
    ] },
  { id: 'production', title: 'Deploy to production', body: 'A Linux Docker host with your own login provider.', icon: 'dns',
    prereqs: ['Linux + Docker 28+', 'Compose v2.24+', '2 vCPU, 4 GB RAM', 'An OIDC client', 'Two hostnames'],
    steps: [
      { title: 'Download a release', body: 'Each release bundle has Compose files that point at its images on GHCR, pinned by digest.', shell: 'bash', code: `curl -fsSLO ${GH}/releases/download/vX.Y.Z/ume-llm-gateway-X.Y.Z.tar.gz
tar -xzf ume-llm-gateway-X.Y.Z.tar.gz && cd ume-llm-gateway-X.Y.Z` },
      { title: 'Generate secrets and certificates', body: 'The script asks for your OIDC authority and public hostnames, then creates an internal CA, certificates, passwords, the key pepper and deploy/.env. It is safe to re-run.', shell: 'bash', code: `./deploy/init-deployment.sh
# behind nginx, NPM or F5:  --proxy
# with your own PKI:        --gateway-cert … --admin-cert …`, note: 'Back up deploy/secrets/.pepper and deploy/certs/data-protection.pfx separately from database dumps. Losing the pepper invalidates every key.' },
      { title: 'Start it', body: 'The production file is hardened: read-only, non-root, on an internal data network. It never seeds data and never runs Keycloak or the fake LLM.', shell: 'bash', code: 'docker compose -f deploy/compose.prod.yaml --env-file deploy/.env up -d --wait' },
      { title: 'Connect your identity provider and providers', body: 'Point UME_OIDC_AUTHORITY at your Keycloak, AD FS or Entra ID. Roles, department codes and AD groups are mapped from claims. Then add providers under Providers & models and use Discover models.', shot: 'providers-discover', shotAlt: 'The Discover models dialog listing models from a provider' },
    ] },
]

/** Was the admin UI's Getting started page: what an application developer needs once they have a key. */
export const ERROR_CODES = [
  ['invalid_api_key', 'The key is missing or unknown.'],
  ['key_expired', 'The key has passed its expiry date.'],
  ['key_revoked', 'The key was revoked, or its rotation grace period ended.'],
  ['key_disabled', 'The key, its team or its department is disabled.'],
  ['address_not_allowed', 'The key may not be used from this client address.'],
  ['model_not_allowed', 'The key may not use this model or alias.'],
  ['model_not_found', 'No alias or model has this name.'],
  ['model_ambiguous', 'Several providers have a model with this name; use provider/model or an alias.'],
  ['budget_exceeded', 'A key, team or department budget has run out.'],
  ['rate_limited', 'Too many requests or tokens per minute for this key.'],
  ['pii_blocked', 'Personal data was found and the key\'s PII policy is Block.'],
  ['attachment_not_allowed', 'The key\'s Attached files setting does not allow this kind of file.'],
  ['request_too_large', 'The body is larger than the gateway accepts.'],
  ['no_eligible_provider', 'No provider is allowed for this key, residency and model.'],
  ['all_providers_failed', 'Every provider and fallback failed or timed out.'],
  ['invalid_request', 'The request body is not valid for this endpoint.'],
]

export const GLOSSARY = [
  ['Key', 'A secret that identifies an application and controls its access, limits and budget.'],
  ['Alias', 'A model name such as ume/chat-standard that the gateway maps to one or more provider models.'],
  ['Fallback', 'The next provider tried when a call fails before the response starts.'],
  ['Budget', 'The most an owner may spend during a calendar period.'],
  ['Residency', 'Where a provider runs: on-prem, EU or external. A key lists the residencies it may use.'],
  ['Routing rule', 'A condition on the request, such as a header or how much of the budget is left, that sends it to a different model. The x-ume-rule response header shows which rule applied.'],
]

/** docs/performance.md, Results (baseline 2026-10-27). All gateway figures on the site come from that one run. */
export const BENCH_DATE = '27 Oct 2026'

export const BENCH_STATS = [
  { label: 'Added latency', value: '0.97 ms', sub: 'Median, non-streaming, 1 client' },
  { label: 'Time to first byte', value: '1.28 ms', sub: 'Streaming, 1 client' },
  { label: 'Peak throughput', value: '17 888', sub: 'Streamed msg/s on 4 vCPUs' },
  { label: 'CPU per message', value: '0.21 ms', sub: 'Gateway process, 4 vCPUs' },
]

/** docs/performance.md, Results (baseline 2026-10-27), Scaling (streaming chat, msg/s). */
export const SCALE = [
  { id: 'clients', tab: 'By clients', label: 'Concurrent clients (4 vCPUs)', x: ['1', '4', '16', '64', '128', '256'], v: [607, 2356, 6179, 12612, 15489, 17888] },
  { id: 'vcpu', tab: 'By vCPUs', label: 'vCPUs (peak)', x: ['1', '2', '4', '6'], v: [4751, 9616, 17772, 20550] },
]

/** docs/production-setup.md, Sizing; measured in docs/performance.md, A small host (2026-10-27). */
export const SIZING = {
  measured: [
    { label: 'Gateway CPU at 50 msg/s', value: '0.39', sub: 'vCPU on average, limit 0.5' },
    { label: 'Gateway memory', value: '236 MB', sub: 'Highest, 100 msg/s, ~560 open streams' },
    { label: 'Added latency', value: 'None', sub: 'Measurable at 50 and 100 msg/s' },
    { label: 'Per usage record', value: '563 B', sub: 'Postgres, with indexes' },
  ],
  rows: [
    ['Gateway', '0.5 vCPU, up to 1', '256 MB, up to 512 MB', '–'],
    ['Admin API and UI', '0.25 vCPU', '256 MB, up to 512 MB', '–'],
    ['Postgres', '0.5 vCPU, up to 1', '512 MB, up to 1–2 GB', 'Usage data'],
    ['Redis', '0.1 vCPU', '64 MB, up to 256 MB', 'None'],
  ],
  totals: [
    ['One host for everything', '2 vCPU', '4 GB', '20 GB + usage data'],
    ['Gateway and admin API only', '1–1.5 vCPU', '1.5 GB', '5 GB'],
  ],
  disk: [['5 msg/s average', '≈ 90 GB'], ['10 msg/s average', '≈ 180 GB'], ['50 msg/s around the clock', '≈ 890 GB']],
}

export const COST_ROWS = [
  { step: 'Key and catalogue lookup', when: 'Before first byte', rt: '0', how: 'In memory; refreshed in the background' },
  { step: 'Rate limit + circuit state', when: 'Before first byte', rt: '1', how: 'One Lua script and one MGET, pipelined' },
  { step: 'Budget reservation', when: 'Before first byte', rt: '1', how: 'One script checks key, team and department budgets' },
  { step: 'Budget reconciliation', when: 'After response', rt: '1', how: 'Pipelined, off the client\'s critical path' },
  { step: 'Usage record', when: 'Background', rt: '0', how: 'Batched, about ten records per Postgres COPY statement' },
]

/** docs/performance.md, Comparison with eneo, eneo v2.2.3 (2026-10-27, same run as the baseline; streaming, 4 vCPUs each). */
export const COMPARE = [
  { label: 'Time per streamed answer, 1 client (median)', max: 68.1, rows: [['Ume gateway', 1.58, '1.6 ms'], ['eneo v2.2.3', 68.1, '68.1 ms']] },
  { label: 'CPU per message, 16 clients', max: 79.2, rows: [['Ume gateway', 0.49, '0.49 ms'], ['eneo v2.2.3', 79.2, '79.2 ms']] },
  { label: 'Postgres statements per message', max: 78, rows: [['Ume gateway', 0.7, '0.7, batched'], ['eneo v2.2.3', 78, '78']] },
]

export const BENCH_CODE = `# All BenchmarkDotNet benchmarks (~10 min)
dotnet run -c Release --project benchmarks\\Ume.LlmGateway.Benchmarks -- --filter *

# Postgres/Redis calls per request + load test
dotnet run -c Release --project benchmarks\\Ume.LlmGateway.Benchmarks -- audit

# Throughput and scaling against a fake LLM, pinned cores
cd benchmarks/compare && PLATFORMS="fake ume" ./run.sh && PLATFORMS="fake ume" ./scale.sh`

export const REPORTS = [
  { title: 'Performance', body: 'Every benchmark result, what a request costs and how to measure it.', href: 'docs/performance/' },
  { title: 'Platform comparison (slides)', body: 'The eneo comparison for non-technical readers. Step with the arrow keys.', href: 'reports/platform-comparison.html', meta: 'HTML · PDF', alt: 'reports/platform-comparison.pdf' },
]
