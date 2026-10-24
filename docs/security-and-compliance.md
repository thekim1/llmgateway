# Security and compliance: POC evidence and approval inputs

As of 2026-10-07. **Not legal advice, a completed DPIA or authorization to process
municipal case data.** DPO, jurist, records manager and information-security owner must
approve each downstream use case and deployment before real data is introduced.

## Technical controls and limits

| Area | Implemented / verified | Remaining operator responsibility |
|---|---|---|
| Minimization | No body/auth-header capture; metadata-only usage; masked audit; log leakage regressions | Disable proxy/provider/APM payload logging and crash dumps; review exported telemetry |
| Keys | HMAC-SHA256 + pepper, show-once, expiry, immediate/grace rotation, invalidation | Secret-store access, expiry policy, compromise procedures and pepper continuity |
| Providers | Encrypted credentials, residency allow-lists and PII-triggered on-prem routing | Contracts, actual region/replication/support access, endpoint egress allow-list |
| PII | Configurable Off/Allow/Redact/Block/Reroute; category counts only | Detectors are heuristic, not anonymization or proof of absence of personal/secret data |
| Admin | Secure HttpOnly BFF cookie with server-side session (ends on sign-out; at most 12 h from sign-in), PKCE, CSRF, scoped RBAC, admin rate limit, headers | MFA/passkeys/conditional access at IdP, claim ownership, account lifecycle |
| Transport | Production HTTPS listeners/upstreams (no automatic redirects); Postgres VerifyFull; Redis TLS-only | Municipal PKI renewal, SANs, trust chains and approved upstream endpoints |
| Database | Migrator DDL owner; separate gateway/admin roles granted table by table (gateway cannot read the audit log or Data API keys, or add Data Protection keys); audit insert-only for admin | No bootstrap credentials in apps; secured backups, DBA access auditing and storage encryption |
| Redis | Default user disabled; password-hash ACL; `ume:*` keys/invalidation channel; forbidden CONFIG/KEYS/FLUSHALL | Redis is shared by the two APIs; rotate both clients together; monitor memory |
| npm packages | Few direct packages; lockfiles with `npm ci` in CI; install scripts off, 7-day age gate on new versions, no git dependencies (`.npmrc`); `npm audit` clean | Review new packages before adding them; watch advisories ([npm packages](#npm-packages)) |
| Containers | Digest-pinned chiseled-extra/infrastructure, nonroot, read-only, dropped capabilities, no-new-privileges | Image/dependency scanning, patch cadence, host hardening and signed release provenance |
| Networks | Internal data network; no DB/cache publication; distinct API listeners, llm network | Compose networks do not replace a firewall, management VLAN or provider egress policy |
| Availability/accounting | Readiness, back-pressure, reserve/reconcile, graceful drain | No HA/durable writer guarantee; define RPO/RTO and reconcile provider bills |

The mandatory Compose override, not the generated base alone, implements production
controls. RAM-only deployment tests verify TLS/ACL/permissions/container/SPA behavior.
Local development deliberately uses generated secrets, demo users and host-run resources:
do not reuse that environment for sensitive processing.

## npm packages

The npm packages are only used to build and test the admin UI, the website and the browser tests; the gateway and the
APIs are .NET and use none of them. The admin UI's built files contain Vue, Vue Router, Pinia and the self-hosted fonts
and icons; everything else stays on the build machine. Since every package can run code where it is installed or
built, the projects keep the list short and limit what a package can do:

- **Few direct packages.** Dialogs, drawers, search and the phone menu use the browser's own `<dialog>` element
  (`src/admin-ui/src/components/ui/ModalDialog.vue`) instead of a UI component library; removing Reka UI took 17
  packages out of the admin UI's runtime code. Before adding a package, prefer a few lines of our own code.
- **No install scripts.** Each project's `.npmrc` sets `ignore-scripts=true`, so a package cannot run code when it is
  installed (the usual way malicious releases steal tokens). None of our packages need one; `npm run` and `npm test`
  still run our own scripts.
- **No brand-new versions.** `min-release-age=7` makes `npm install` and updates skip versions published in the last
  7 days, when hijacked releases are usually found and removed. `npm ci` installs exactly what `package-lock.json`
  says and is not affected, so CI and releases never change on their own.
- **Registry only.** `allow-git=none` refuses dependencies fetched straight from git repositories.
- **Locked and audited.** CI installs with `npm ci` from the committed lockfiles. Run `npm audit` in `src/admin-ui`,
  `site` and `tests/e2e` when updating packages.

What remains, and why: the build tool (Vite with Rolldown and Lightning CSS) and Tailwind ship one native binary per
operating system, and GitHub's dependency graph lists every variant, although a machine only installs its own (about
50 of the admin UI's lockfile entries). ESLint with the Vue and accessibility rules, and Vitest with jsdom and Vue Test
Utils, are most of the rest; they only run on developer machines and in CI, and they catch accessibility and logic
errors before release. Vue Router stays on version 5: version 4 has fewer dependencies but no longer gets fixes.

## Legal decision matrix

| Framework | Required decision / evidence |
|---|---|
| GDPR + Dataskyddslag (2018:218) | Identify controller, processor/subprocessors, purpose and Article 6 basis; Article 9/10 conditions where applicable; necessity/minimization; Art. 28 contracts, Art. 32 measures, Art. 35 DPIA screening |
| OSL (2009:400) | Determine secrecy by use case and disclosure/access consequences, including support and foreign provider access; residency badges do not authorize disclosure |
| Arkivlagen (1990:782), public access rules | Determine which metadata, audit and downstream records are allmän handling; agree retention/gallring, preservation/export and disclosure review |
| Cybersäkerhetslag (2025:1506), NIS2 | Municipal scope, accountable owner, risk/supply-chain management, registration/supervision and incident-reporting process; coordinate with existing municipal ISMS |
| EU AI Act (EU) 2024/1689, as amended | Classify the actual downstream system/intended purpose and municipal provider/deployer role; literacy, transparency, human oversight, applicable record obligations and fundamental-rights assessment |
| GDPR Chapter V / Schrems II / EU-US DPF | Identify transfers and remote access; evaluate lawful mechanism, current adequacy/certification, transfer impact assessment and supplementary measures |
| DOS-lagen (2018:1937), EN 301 549 | Assess legal scope, publish an approved accessibility statement, manual assessment and reporting channel |

The gateway is transport/governance infrastructure, not evidence that an AI-assisted
welfare, education, HR or case-management use is low risk. It does not make decisions.
Metadata-only accounting is not a substitute for downstream legally required records.
Keep those records in an approved case system rather than enabling gateway content logs.

**Timeline caution:** the Commission's current AI Act page describes the July 2026 AI
Omnibus amendments, general applicability from 2 August 2026 and revised high-risk
timelines (Annex III: 2 December 2027; regulated-product systems: 2 August 2028).
Jurists must check the applicable consolidated legal text, transitional provisions and
use-case-specific obligations. Do not rely on the old blanket "all high-risk August 2026"
assumption. The Swedish cybersecurity act is in force in 2026 and expressly covers
municipalities; confirm the applicable reporting authority/instructions before launch.

## DPIA input sheet (complete per use case)

| Field | Input / open decision |
|---|---|
| Processing owner / DPO / security owner | Named municipal function, contact, approval date: **TBD** |
| Purpose and necessity | Describe concrete workflow, benefits and non-AI alternative |
| Data subjects / data categories | Residents, staff, children/vulnerable persons; special-category/criminal/secret data? |
| Data flow | Client -> gateway memory -> approved provider -> client; metadata -> municipal DB/telemetry; IdP claims -> BFF |
| Basis / legal duties | Art. 6 basis, any Art. 9/10 condition, OSL/gallring classification |
| Recipients and transfers | Exact provider/legal entity/subprocessors, location, support access, retention/training settings |
| Scale and retention | Request volume, key owners and usage identifiers; policy per data class, backup expiry |
| Risk: confidentiality | Missed PII/secret content, incorrect residency tag, stolen key, malicious admin/provider |
| Risk: accuracy/fairness | Hallucination, biased output, automation bias, unauthorized decision use |
| Risk: rights/availability | Access/correction handling, disclosure, exclusion, budget denial/outage, lost accounting |
| Mitigation | On-prem-only policy for unapproved data; MFA/RBAC; TLS; secret isolation; human review; logging minimization |
| Residual risk / sign-off | Owner assesses likelihood/impact, remaining treatment, consultation with IMY where needed |

## Draft record of processing (Article 30 input)

- **Controller:** relevant Umeå kommun body; contact and DPO: operator to complete.
- **Purpose:** authorized AI API access, allocation/budget control and security administration.
- **Data subjects:** administrators, key owners and people described in client content.
- **Categories:** transient client content; persistent organization/user identifiers,
  key HMAC/prefix, usage/cost/time/status, policy-category counts and audit actor/mutation.
- **Recipients:** approved providers and authorized municipal IT/department administrators;
  IdP and contracted infrastructure/subprocessors as applicable.
- **Transfers:** none permitted merely because a provider is labelled EU; document actual
  access/transfer pathways and mechanism for every recipient.
- **Retention:** a nightly job removes usage records, refused-key records and ended budget alerts after
  **365 days** and audit entries after **365 days** by default (admin UI *Settings > Data retention*, 90–3 650 days; the
  audit log can be kept for ever). Monthly usage totals are kept. **Set both from the approved records/retention
  decision (gallringsbeslut) before production**: usage metadata and the audit log may be allmänna handlingar, and
  removal cannot be undone except from a backup. Every settings change and manual run is in the audit log.
- **Safeguards:** controls above; TLS, least privilege, certificate-protected key ring,
  metadata-only logging, segregated listeners/networks, backups and incident procedures.

## Release approval gate

Use synthetic test data only until the DPIA/OSL/transfer/provider contract reviews,
records retention, manual accessibility review, restoration drill, incident exercise,
dependency/image/SBOM review and operational ownership are signed off. Automated test
success does not waive these decisions.

## Authoritative references

- [GDPR, EUR-Lex](https://eur-lex.europa.eu/eli/reg/2016/679/oj)
- [Dataskyddslag](https://www.riksdagen.se/sv/dokument-och-lagar/dokument/svensk-forfattningssamling/lag-2018218-med-kompletterande-bestammelser-till_sfs-2018-218/)
- [OSL](https://www.riksdagen.se/sv/dokument-och-lagar/dokument/svensk-forfattningssamling/offentlighets--och-sekretesslag-2009400_sfs-2009-400/)
- [Arkivlagen](https://www.riksdagen.se/sv/dokument-och-lagar/dokument/svensk-forfattningssamling/arkivlag-1990782_sfs-1990-782/)
- [Cybersäkerhetslag 2025:1506](https://www.riksdagen.se/sv/dokument-och-lagar/dokument/svensk-forfattningssamling/cybersakerhetslag-20251506_sfs-2025-1506/)
- [AI Act](https://eur-lex.europa.eu/eli/reg/2024/1689/oj), [Commission timeline](https://digital-strategy.ec.europa.eu/en/policies/regulatory-framework-ai),
  [AI Omnibus text](https://eur-lex.europa.eu/legal-content/EN/TXT/?uri=OJ:L_202601744)
- [IMY: data protection impact assessments](https://www.imy.se/verksamhet/dataskydd/det-har-galler-enligt-gdpr/konsekvensbedomningar-och-forhandssamrad/)
- [DIGG: digital accessibility](https://www.digg.se/digital-tillganglighet)

**Virtual key reveal.** Besides the HMAC hash used for authentication, the full key is stored encrypted (ASP.NET Core Data Protection, same key ring as provider credentials) so a gateway-admin can show or copy it later. The UI masks it by default, only `gateway-admin` can call `POST /api/keys/{id}/reveal`, and each reveal or copy is audit-logged with the actor (the key itself is never written to the log). A database dump together with the Data Protection key ring can therefore recover keys; protect both as secrets.
