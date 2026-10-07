# Architecture decision records

| ADR | Decision | Rationale / consequences |
|---|---|---|
| 001 | Separate .NET data/control planes | Gateway has no admin routes; Admin API never forwards inference bodies. Separate listeners/roles limit blast radius, but both use shared DB/Redis. |
| 002 | Vue + OIDC BFF, not browser bearer tokens | Secure cookie, code+PKCE, CSRF; IdP owns MFA. SameSite=Lax supports OIDC redirects. SaveTokens=false limits leakage; some IdPs ask logout confirmation. |
| 003 | Metadata-only accounting and masked audit | No prompt/response persistence, even for troubleshooting. Downstream legal case records stay in the approved case system; identifiers can still be personal/public-record data. |
| 004 | Virtual-key HMAC and certificate-protected DP credentials | Plain key shown once, pepper outside DB. DP key ring shared in DB and encrypted in production. Pepper/certificate loss requires recovery or key replacement. |
| 005 | Redis atomic reserve/reconcile | Shared rate/budget/circuit state and invalidation across services; restrict namespace/commands. In-memory mode is only for local/test environments. |
| 006 | JSON passthrough, data-driven provider/model profiles | New model families do not require a release. Rewrites/translation apply only supported fields; provider contract tests are still needed. |
| 007 | Rotation retains budget lineage | Immediate revoke default, optional 24h grace; both predecessors/replacements share spend so rotation cannot evade budgets. |
| 008 | File-based Aspire, Compose generated + mandatory override | Aspire is source of resource graph; approved override supplies deployment-specific TLS/ACL/roles/network/container controls. Never deploy base alone or containerize AppHost. |
| 009 | Production external IdP/provider; local test services only in run mode | Dev realm/FakeLlm/Vite are not production workloads. Production images contain static Vue assets served by BFF. |
| 010 | Accessibility AAA design goal with explicit evidence boundary | Four themes, contrast/component/browser automation; manual screen-reader/usability/IdP review needed, no automated certification claim. |
| 011 | Chiseled-extra and strict transport | Nonroot, no shell, ICU/time-zone data for Swedish budgets. HTTPS probes use a dedicated process mode; municipal PKI/secret delivery required. |
| 012 | Testcontainers integration fallback | File-based AppHosts are not supported by DistributedApplicationTestingBuilder. Real Postgres/Redis/Kestrel tests plus real-stack Playwright cover runtime behavior. |

Accepted for POC on 2026-10-07. Reassess durable accounting, HA, egress isolation,
retention automation and per-use-case legal controls before production approval.
