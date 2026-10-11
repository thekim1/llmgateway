# Manual test checklist

Things built since the last manual test round that only a person with the real deployment (Foundry, Keycloak, Redis ACL,
real traffic) can confirm. Tick items off as you go; delete the file or empty the list when a round is done.

## Already built (from earlier sessions)

- [ ] **Migration startup log:** a fresh database starts without the `42P01 DataProtectionKeys` error in the migration log (commit e9871ad).
- [ ] **Budget refunds with the Redis ACL:** no more `NOPERM ... 'decrby'` in the gateway log after a failed or cancelled request
  (redeploy with the updated `deploy/redis/users.acl.example` ACL) (commit d31f5e3).
- [ ] **Costs with 2 decimals:** spend, budgets and usage show öre (`12,34 kr`), not whole kronor (commit d31f5e3).

## Foundry and prices (uncommitted)

- [ ] **Foundry provider from the project URL:** create a provider of type *Azure AI Foundry* with the portal's project endpoint
  `https://<resource>.services.ai.azure.com/api/projects/<project>` and the *API key header*. The base URL stays as entered; a chat request to a deployment works (the gateway sends it to `…/openai/v1`).
- [ ] **Save and discover:** edit the seeded *ai-foundry* provider, paste your URL and key and click *Save and discover models* without saving first;
  it saves and the dialog lists your deployments.
- [ ] **Same model on two providers:** after the upgrade the gateway starts without `An item with the same key`. Both
  `DeepSeek-V4.1-Flash` models are listed (Providers > Models says *clients call <provider>/…*); a request to the plain name gets
  `400 model_ambiguous` naming both; `<foundry>/DeepSeek-V4.1-Flash` and `<ollama>/deepseek-v4.1-flash` each work and go only to that
  provider (break one provider: the request fails, no fallback). Then create a route `deepseek-v4.1-flash` with both targets:
  the plain name works and falls back in the route's order.
- [ ] **Discover models lists only deployments:** *Discover models* shows your deployed models (deployment names), not the whole catalogue, and no Claude.
- [ ] **Suggested prices:** GPT/o-series/embedding deployments show *Azure list price* with the meters it came from; PTU/batch and Grok show none.
  Compare one or two with the Azure pricing page.
- [ ] **Claude on Foundry:** a second provider of type *Anthropic* with the same project endpoint as base URL and the API key header;
  *Discover models* lists only the Claude deployments; a chat completion through the gateway to a Claude deployment works (streaming too).
- [ ] **GPT-5/6 reasoning:** a GPT-5 deployment gets the reasoning parameter profile; a request with `max_tokens` and `temperature` succeeds.
- [ ] **Responses API:** with *Responses* ticked on the Foundry provider, `POST /v1/responses` to a GPT-5 deployment works.
- [ ] **No price access:** if the admin API cannot reach `prices.azure.com`, discovery still works and logs one warning.

## Provider status and stability (uncommitted)

- [ ] **Status no longer sticks for 24 h:** the provider that showed *Degraded* while answering now shows *Healthy* (or *No recent traffic*) on
  Overview, with a reason on hover ("N of M requests failed at the provider in the last hour …"); Operations shows the same reason as text.
- [ ] **Client errors do not count:** send a few requests with an invalid parameter (provider answers 400) and cancel a streamed request;
  *Provider errors (24 h)* stays at 0 while *All failed (24 h)* rises, and the status stays *Healthy*.
- [ ] **Provider failures do count:** with at least 10 requests in the hour, make the provider fail (wrong port / stopped on-prem server with a
  single-target route); the status becomes *Degraded* or *Down* with the matching reason, and returns to *Healthy* about an hour later.
- [ ] **Provider stability page:** *Operations > Provider stability over time* (or the link under the Overview providers card) shows p95 latency
  and provider error rate per provider for 24 hours, 7 days and 30 days; hours and days match Swedish time; *View as table* lists the same values;
  the ranking puts the provider with the fewest provider errors first.

## Data API keys (uncommitted)

- [ ] **Create:** as gateway-admin open *Access → Data API access*, create a key with *Usage totals* only. The secret dialog shows `ume-dk-…` and a curl/PowerShell example with the Data API address (local: `https://localhost:7311`).
- [ ] **Use:** `curl -H "Authorization: Bearer <key>" "<data-api>/v1/usage/aggregate?from=<first of month>&to=<today>&granularity=day"` answers 200; `/v1/usage/records` answers 403; the same with `-H "X-Api-Key: <key>"` works.
- [ ] **Log and last use:** the Data API log has `data.read` with client `key:<name>`; after a request the list shows *Last used* (refresh the page).
- [ ] **Edit:** add *Usage records*; within 30 s `/v1/usage/records` answers 200.
- [ ] **Revoke:** revoke the key; within 30 s requests answer 401 and the Data API logs `data.auth.failed` with `key_revoked` and the prefix (never the key). Then *Delete* removes it; the audit log shows create, update, revoke and delete for `DataApiKey`.
- [ ] **Access:** a department-admin does not see *Data API access* in the menu and `/data-access` shows *No access*.
- [ ] **OIDC unchanged:** a token from the `ume-data-dev` client still works.
- [ ] **Production:** after deploying, the migration log shows the grants applied (no `42501` from the Data API when a key is used); set `UME_DATA_PUBLIC_URL`.

## Routing rules: shared rules, templates, switch (uncommitted)

- [ ] **Templates after migration:** *Routing > Routing rules* lists four global rules marked *Template*, all switched off, each with a short description.
- [ ] **Template blocked until its route exists:** switch on *Personal data to a local model*: it stays off and the message above the list names `local`.
  Create a route alias `local` (pointing at the on-prem model), switch it on again: it turns on and the *Template* mark is gone.
  With **Test a request** + *Personal data found* it now routes to `local`. Switch it off again afterwards.
- [ ] **Switch with the keyboard:** Tab to a rule's switch in the list, press Space: it toggles (screen reader / devtools show `role=switch`, `aria-checked`).
- [ ] **Shared rule:** *New rule* → scope *Key*, tick two keys in the list (use the filter box), save. Send a matching request with each key
  (`x-ume-rule` header set for both) and with a third key (no header).
- [ ] **New rule from template:** select a template → *New rule from template*; the form is prefilled; choose scope *Team* and two teams; save.
- [ ] **Deleting a shared team:** delete one of those teams in *Organisation*: no question for the shared rule (it keeps the other team);
  a rule only that team had still asks *Deactivate/Delete*.
- [ ] **Existing scoped rules survive the migration:** a key/team/department rule created before the upgrade still applies to the same owner.

## PII: personnummer and incidents (uncommitted)

- [ ] **Policy on:** set a test key's PII policy to *Redact* (the default *Off* scans nothing).
- [ ] **Personnummer formats:** chat with `900101-1234`, `900101 1234`, `19900101-1234` and `199001011234` (made-up numbers, wrong check digit),
  once with `content` as a string and once as a content-part array: the model receives `[PERSONNUMMER]` and the response has `x-ume-pii: redacted`.
  The valid test number `19121212-1212` is caught too.
- [ ] **Phone numbers stay phone numbers:** `Ring 0701234567` becomes `[TELEFON]`, not `[PERSONNUMMER]`.
- [ ] **Chat client:** the same in your chat UI (Open WebUI or similar) through the gateway, with streaming on.
- [ ] **PII incidents page:** *Monitor > PII incidents* lists those requests with time, key, team, category and action; the filters and tiles work;
  clicking a request id opens it on *Usage* with *PII found*.
- [ ] **Detected only:** switch the key to *Allow* and send an email address: it is listed as *Detected, sent on*.
- [ ] **Empty period:** a period without incidents shows the explanation that scanning is per key.
- [ ] **Department admin:** sees only incidents from their own departments.

## Whole release

- [ ] **Upgrade the deployed instance:** the migration `SharedRulesDataKeysPii` runs once; gateway and admin API are deployed together
  (the routing rule API changed from `scopeId` to `scopeIds`, see docs/upgrade-guide.md).
