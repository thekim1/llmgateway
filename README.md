# Umeå kommun LLM gateway POC

.NET 10 data plane and OIDC/BFF control plane, Vue 3/Pinia admin UI, PostgreSQL,
authenticated Redis and Aspire 13.6. This is a demonstrator, **not a production approval,
legal compliance attestation or WCAG AAA certification**.

The gateway provides virtual keys, department/team/key budgets in SEK, provider fallback,
residency restrictions and optional PII policy. It stores usage metadata, never prompt or
response content. Providers/models are configuration, not hardcoded model-family lists.

## Local development

Windows: .NET SDK 10.0.401, Aspire 13.6, Docker 28+, Node 24.15+ recommended.
Use PowerShell 5.1 and run commands separately (or chain with `;` and exit-code checks).

```powershell
Set-Location src\admin-ui
npm ci
Set-Location ..\..
aspire start --non-interactive
aspire wait admin-ui
```

The UI is `https://localhost:5173`. Local Keycloak test users/roles are provisioned by
the realm import; get development passwords through Aspire's secret store without
printing them, enabling transcripts or saving browser auth state. Ollama is opt-in:
set `Ollama__Enabled=true` before starting. FakeLlm is the default test provider.
Stop **this exact AppHost** before backend/AppHost edits or builds:

```powershell
aspire stop --apphost src\Ume.LlmGateway.AppHost\Ume.LlmGateway.AppHost.csproj --non-interactive
dotnet build UmeLlmGateway.slnx -v q -nologo
dotnet test --solution UmeLlmGateway.slnx
Set-Location src\admin-ui
npm run lint -- --max-warnings 0
npm run typecheck
npm test
```

Browser tests require the running Aspire stack:
`Set-Location tests\e2e; npx tsc --noEmit; npx playwright test --max-failures 1`.
They do not save screenshots, videos, traces or storage state.

## Deployment and documentation

Generate Compose with
`aspire publish -o deploy\generated --non-interactive`.
**Never deploy the generated base alone**: use the mandatory hardening override and
operator-mounted secrets/certificates in [the runbook](docs/runbook.md).
`.\deploy\Test-Deployment.ps1` builds and verifies an isolated production-shaped stack
with RAM-only test secrets and cleans up only its own resources.

Open `UmeLlmGateway.slnx` in Visual Studio and select `Ume.LlmGateway.AppHost` as
the startup project. Its `Properties\launchSettings.json` contains the HTTPS/HTTP
dashboard profiles; root `aspire.config.json` selects the same project for CLI commands.
The original development secret-store ID is preserved. When migrating an existing
checkout, set `DevelopmentVolumes:Postgres` (and `DevelopmentVolumes:Ollama`, if used)
in AppHost user secrets to the previous Docker volume names before starting, so existing
development data is reused. These optional overrides apply only in local run mode.

| Document | Purpose |
|---|---|
| [Implementation plan](docs/implementation-plan.md) | Approved scope |
| [Admin API](docs/admin-api.md) | UI/API contract |
| [Architecture](docs/architecture.md) | Components, trust boundaries, tradeoffs |
| [Security and compliance](docs/security-and-compliance.md) | Controls, DPIA and processing-record inputs, release gates |
| [Accessibility statement](docs/accessibility-statement.md) | Automated evidence and outstanding manual checks |
| [Runbook](docs/runbook.md) | Deployment, credentials, backups, incidents |
| [Upgrade guide](docs/upgrade-guide.md) | Migration, rollback, certificates |
| [ADRs](docs/adr.md) | Key design decisions |
| [Demo script](docs/demo-script.md) | Safe repeatable demonstration |
| [Handover](HANDOVER.md) | Latest verified continuation checkpoint |
