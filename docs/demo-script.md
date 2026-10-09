# Safe demo script

Use **synthetic inputs only** and the local Aspire/FakeLlm stack. No terminal transcripts,
screen recordings, browser traces, screenshots of show-once dialogs or saved plaintext keys.
Do not copy dashboard login tokens into documents. A human may keep a show-once key in
memory during the demo and then revoke it; never paste it into shell history.

For a clean environment without demo data, start with `.\scripts\Start-Dev.ps1 -Empty` (see README).

1. Start `src\Ume.LlmGateway.AppHost\Ume.LlmGateway.AppHost.csproj` via Aspire and wait for admin-ui. Sign in through real
   Keycloak as gateway-admin using the development secret store without printing credentials.
2. Create a synthetic department/team and key in Organisation/Nycklar. Review the PII,
   model/residency and limit fields. Acknowledge show-once handling; demonstrate that the
   secret cannot be retrieved from a later key-details page.
3. Make a request in a nonpersisting client to `ume/demo-fallback`. Inspect only safe
   response headers: selected fake-eu and `x-ume-fallbacks: 1`, plus request_id. Do not print
   or save the body. Open Användning and locate metadata/cost using request_id.
4. With RerouteToOnPrem, submit a synthetic detector-valid personnummer fixture held in
   memory to `ume/chat-standard`. Verify `x-ume-residency: OnPrem`/fake-onprem; category
   counts in usage contain no matched value. Explain false positives/negatives.
5. Set a zero budget and verify 402 `budget_exceeded`. Immediately rotate the key and
   verify the replacement is still blocked: rotation does not reset the budget.
6. Routing rules: in **Routing > Routing rules** open *Premium via header* (demo data). Send a synthetic request to
   `ume/chat-standard` with the header `x-ume-tier: premium` and inspect only `x-ume-rule` and `x-ume-provider`; without the header
   no rule applies. Use **Test a request** with the same header to show why, and with a missing header to show "could not be
   evaluated". Create a team with a rule, then delete the team and show the question: deactivate the rule, find it under
   *Needs owner* and assign it to another team.
7. With a separate unrestricted synthetic key, rotate with 24h grace. Both keys work;
   revoke the predecessor and verify only the replacement remains usable.
8. Show the three themes (light, dark, Lumen), keyboard focus, the mobile layout and data tables.
   Explain automatic checks and remaining manual assessment; do not claim AAA certification.
9. Show Drift/Hälsa: component status, actual gateway version, live usage queue, schema,
   provider statistics. Demonstrate audited drain/circuit/cache action with confirmation.
10. Sign in as viewer/department-admin to show read-only and department isolation. Explicitly
   renew the BFF session, then logout through the IdP confirmation/callback.
11. Revoke synthetic demo keys, inspect masked audit and stop the exact AppHost cleanly.
    Retain/delete metadata only under the approved POC records policy; do not wipe the volume.

Automated browser evidence is in `tests\e2e` (run on the separate test database, see the README): five user
journeys, the final synthetic demo, routing rules checks (journeys, UI details, permissions, focus) and nine accessibility checks. On 2026-10-09,
all 34 passed. There are also six real-container integration tests (fallback, PII on-prem, budgets, rotation,
accounting, rate limits and invalidation). Production-shaped TLS/ACL/roles/SPA verification
is `deploy\Test-Deployment.ps1`; it does not claim production IdP/provider approval.
