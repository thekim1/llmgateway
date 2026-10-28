# Admin UI

## Finding your way

The side menu groups pages by the job you came to do. You only see the pages your role allows.

| Group | Pages | Use it to |
| --- | --- | --- |
| Monitor | Overview, Usage, Health & operations | See spend, traffic and provider health; act on anything that needs a decision |
| Access | Keys, Organisation, Data API access | Give applications and reporting tools access, and set up departments and teams |
| Models & routing | Catalogue, Providers & models, Routes, Routing rules | See which models can be called, connect providers, and decide where requests go |
| Cost & compliance | Budgets & alerts, PII incidents, Audit log | Limit spend, follow up personal data findings and see who changed what |
| System | Settings | Trusted proxies, data retention, request logging and version |

**Search** (the search box at the top, or **Ctrl K** / **⌘ K**) finds pages and the parts inside them, also by words
that are not in the page name: *retention* opens **Settings › Data retention**, *circuit* opens **Health & operations**,
*department* opens **Organisation**. Use the arrow keys and Enter, or click. Esc or Ctrl K again closes it.

**Team admins** (role `team-admin`) see **Keys** and **Catalogue** and start on Keys. They create, edit, rotate and revoke
the keys of their own teams, with every key setting a department admin has; the team picker in *Add key* lists only those
teams. A team gets team admins when a department admin or gateway admin gives it a **Team code** (*Organisation › Edit
team*) that is in the team admins' `teamCodes` claim; see [Identity provider and roles](identity-and-roles.md#team-scope).

**On a phone** the bar at the bottom has Overview, Alerts, Keys and Health, plus **Menu**. Menu opens every page (same groups
as the side menu), search, the theme switch, your session and **Sign out**. Close it with ×, Esc, a tap outside, or by
choosing a page. Search opens full screen with the field at the top, so the keyboard never covers the results; **Cancel**
closes it.

## Provider status and stability

The **Providers** card on Overview and the provider table on **Health & operations** show each provider's status. The admin API
computes it each time the page loads (Health & operations has *Refresh*; nothing is cached or stored):

- **Circuit open** while the gateway's circuit breaker is open (by default 5 transient failures within 60 s open it for 30 s; an
  admin can open or close it for 15 minutes), **Disabled** for a disabled provider.
- Otherwise from the **last hour**: with no requests *No recent traffic*; with fewer than 10 *Healthy* (too few to judge);
  otherwise **Degraded** when 5 % or more of the requests failed at the provider and **Down** from 50 %. The reason
  ("4 of 40 requests failed at the provider in the last hour (10 %).") is shown next to the status.
- Only **provider errors** count: 5xx answers, timeouts and streams that broke off. Client errors the provider rejected
  (4xx), cancelled requests, the gateway's own internal errors and requests the gateway refused (budget, rate limit, PII)
  do not. A burst of failures therefore stops affecting the status an hour later.

The 24-hour columns (requests, provider errors, all failed requests, first token p50 and p95) come from the same usage
records. Speed is the **provider's time to first token** (`UsageRecords.ProviderFirstTokenMs`): on a streamed answer, from
sending the request to the provider that answered until its first event arrived. It measures how quickly the provider
responds, not how long the prompt or the answer is: failed attempts at other providers, retries and the gateway's own time
are left out, and so is the rest of the stream. The whole request's latency (`LatencyMs`) is not used here because it
grows with the length of the answer and includes fallback attempts. Answers that are not streamed only arrive once
complete, so they have no comparable figure and are not counted; a provider that streamed nothing in the period shows
*—*. Requests recorded before 0.8.2 have no provider first token. The gateway writes one record per request, on the provider
that answered (or failed last); a provider that failed and was replaced by a fallback is not charged with that failure
in the statistics (only its circuit breaker sees it); a request on which every provider failed (502/504
`all_providers_failed`) is recorded on the last provider tried, so a provider that is down shows up as failing.

**Health & operations › Provider stability** shows the p95 time to first token and the provider error rate per provider over the
last 24 hours or 7 days (per hour) or 30 days (per day, Swedish time), plus a ranking with the most stable provider first
(availability, then p95 time to first token). Each chart has a text summary and a table view. The data comes from `GET /api/ops/providers/stats`
([docs/admin-api.md](admin-api.md)).

## Usage dashboard

**Monitor > Usage** opens on a **Dashboard** tab with a quick overview of how the gateway is used and how it performs. The
earlier summary, request list and request lookup are on the **Requests and costs** tab (`/usage?tab=requests`). The
dashboard has a time range (24 hours, 7 days, 30 days, 90 days or custom dates; up to two days per hour, otherwise per
day in Swedish time) and filters for department, team, key, model, provider (gateway-admin), outcome and endpoint, which
apply at once. Department admins and viewers see only their own departments. Nine cards, each with a bar, line and table
view:

| Card | Shows |
|---|---|
| Cost | Cost in **USD** per bucket, stacked by requested model (the 8 busiest models, the rest as *Other models*) |
| Requests | Success, provider error, refused by the gateway (budget, rate limit, PII, invalid) and cancelled; error and fallback share, and *Show failed requests*, which opens the request list with the dashboard's period and filters (not the endpoint) |
| Tokens | Uncached input, cached input and output tokens |
| Provider cache hit rate | Cached ÷ input tokens over the period (prompt caching at the provider) |
| Requests by model | Requests per requested model, same colours as the cost card |
| Latency | Avg, P50, P95 and P99 of the whole request as the client saw it |
| Gateway overhead | Avg, P50, P95 and P99 of the gateway's own time: latency minus time spent waiting on providers |
| Time to first token | Avg and P95 for streamed answers: until the first event reached the client |
| Throughput | Output tokens per second of provider time, over successful requests with output |

To make overhead and time to first token possible the gateway records two more timings per request (`UsageRecords.UpstreamMs`,
`TimeToFirstTokenMs`): provider time is every attempt, failed fallback attempts included, plus reading a streamed answer.
Timings leave out live audio sessions (their latency is the session length) and requests refused before a provider was
called. Requests recorded before the upgrade have no overhead or time to first token. Usage older than the retention
period is only kept as monthly totals without timings, so the charts start at the oldest kept record (the page says so).
The data comes from `GET /api/usage/metrics` ([docs/admin-api.md](admin-api.md)); the query grows with the number of
requests in the period, so long periods on a busy gateway take a few seconds (see [docs/performance.md](performance.md)).

### Finding failed requests

The **Requests and costs** tab filters the request list by model, provider (gateway-admin, plus *No provider (refused)* for
requests the gateway refused before calling one) and **outcome**: *All errors* lists every request that did not succeed,
or pick one outcome (provider error, over budget, rate limited, PII blocked, rejected, cancelled). The error code is shown
under each outcome. In the summary table each **Errors** count is a link that lists that row's failed requests (its
department, team, key, model, provider or day); *Show the failed requests* under the summary lists all of them. Model,
provider and outcome only filter the request list, not the summary. On **Overview**, the error count and error rate of
the last 24 hours link to the failed requests of yesterday and today. Other pages can link to the list with
`/usage?tab=requests&outcome=errors&from=yyyy-mm-dd&to=yyyy-mm-dd` (also `departmentId`, `teamId`, `keyId`, `model`,
`provider`); the filters are applied once and dropped from the address.

## Routing rules page

**Models & routing > Routing rules** (gateway-admin) manages [routing rules](routing-rules.md): rules are listed by
scope in the order the gateway checks them, each with its title, short description and an on/off switch, and the selected
rule's owners, condition, weighted targets and fallbacks on the right. A rule can be shared by several keys, teams or
departments, so the same rule does not have to be set up for each key. Four disabled **template** rules (personal data to a
local model, header opt-in, large prompts, budget nearly used) show how rules are written; switch one on once its target route
exists, or start a new rule from it. From there you can create and edit rules (the condition is validated as you type), switch a rule on or off, move it
earlier or later, change its owners and delete it. **Test a request** runs a sample request against the stored rules and
shows which rule applies and why. When you delete a team or department in **Organisation** that has routing rules, the UI asks
whether to delete the rules or deactivate them until a new team or department is assigned; deactivated rules show up under
**Needs owner**. Usage shows which rule routed a request.

## Version and new releases

**Settings > Version** (gateway-admin) shows the version the admin API runs and the newest release on GitHub, with a link to
its release notes. When a newer release exists the card says so and **Settings** in the side menu gets a **New** badge (a dot
when the menu is collapsed). Local builds show *Development build* and are never reported as out of date. The admin API asks
GitHub at most every 6 hours; with `Admin:CheckForReleases=false` (no internet access) only the running version is shown.
The data comes from `GET /api/settings/version` ([docs/admin-api.md](admin-api.md)).

## Model prices

**Settings > Model prices** (gateway-admin) shows the daily models.dev update: when the admin API last read the list, how
many models follow it, how many prices it changed and how many models it gave a description, capabilities or context
window, or why it failed. **Check now** runs it at once.

In **Providers › Models** each price says where it comes from: *models.dev*, *Set by hand*, or *Set by hand (not in
models.dev)*; the **Price** filter shows one kind. A model that follows models.dev has **Add price** turned off: turn off
**Price from models.dev** in the model drawer, or **Prices from models.dev** in the provider drawer for all its models, to
set your own. The price history has a **Source** column: the models.dev entry a price came from, or *Admin*. A model's
**Description** is shown in the catalogue. See [List prices](providers-and-models.md#list-prices).
