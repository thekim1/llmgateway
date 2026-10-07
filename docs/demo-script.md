# Safe demo script

Use **synthetic inputs only** and the local Aspire/FakeLlm stack. No terminal transcripts,
screen recordings, browser traces, screenshots of show-once dialogs or saved plaintext keys.
Do not copy dashboard login tokens into documents. A human may keep a show-once key in
memory during the demo and then revoke it; never paste it into shell history.

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
6. With a separate unrestricted synthetic key, rotate with 24h grace. Both keys work;
   revoke the predecessor and verify only the replacement remains usable.
7. Show the four themes, Swedish/English, keyboard skip/focus, 320px reflow and data tables.
   Explain automatic checks and remaining manual assessment; do not claim AAA certification.
8. Show Drift/Hälsa: component status, actual gateway version, live usage queue, schema,
   provider statistics. Demonstrate audited drain/circuit/cache action with confirmation.
9. Sign in as viewer/department-admin to show read-only and department isolation. Explicitly
   renew the BFF session, then logout through the IdP confirmation/callback.
10. Revoke synthetic demo keys, inspect masked audit and stop the exact AppHost cleanly.
    Retain/delete metadata only under the approved POC records policy; do not wipe the volume.

Repeatable automated demo evidence is in `tests\e2e` (five user journeys, a final synthetic
demo and six accessibility tests) and six real-container integration tests (fallback, PII on-prem, budgets, rotation,
accounting, rate limits and invalidation). Production-shaped TLS/ACL/roles/SPA verification
is `deploy\Test-Deployment.ps1`; it does not claim production IdP/provider approval.
