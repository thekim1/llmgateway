# Routing rules

Routing rules decide **where a request goes** based on what the request looks like: a header, the
team, how much of the budget is used, whether it contains personal data. They sit in front of the
existing model aliases (see *Routes* in the admin UI) and are modelled on
[Bifrost routing rules](https://docs.getbifrost.ai/providers/routing-rules).

> **Status.** Rules are stored, compiled, **applied to live requests** and managed through the admin API and
> the admin UI (**Routing > Routing rules**).

## Worked examples

Both examples are global rules created under **Routing > Routing rules** (gateway-admin) with the demo data. Your applications keep
asking for the same model name; the rules decide where the request goes. The screenshots were taken from the real UI.

### Example 1: use another model if the main model doesn't answer

*Ask for `ume/chat-advanced`. If every provider behind it fails or times out, try `ume/chat-standard` instead.*

| Field | Value |
|---|---|
| Name | Advanced model with fallback |
| Applies to | Global |
| Condition | `model == "ume/chat-advanced"` |
| Send to | `ume/chat-advanced` (weight 1) |
| Fallbacks | 1. `ume/chat-standard` |

![The new rule form with the condition, the target and one fallback](images/routing-fallback-form.png)

After **Create rule** the rule shows up in the list, in the order the gateway checks it:

![The saved rule: condition, target and "Then, if those fail" fallback](images/routing-fallback-rule.png)

How it behaves: the target is tried first (with its own provider order from the route). The fallbacks are tried only when the call fails
in a way that can be retried (a timeout, 408, 409, 429, a 5xx or a provider configuration error), and never after the response has started
streaming. Other client errors (such as 400) are returned as they are. The response header `x-ume-fallbacks` counts the extra attempts and
`x-ume-rule` names the rule. Providers whose circuit breaker is open are moved last, so the fallback is tried before a known-broken primary.

### Example 2 (advanced): chats with personal data go to an on-prem model

*When the gateway finds personal data in a chat (a personnummer, an email address, a phone number, an IBAN), send the request to
`ume/chat-onprem` instead of an EU or external provider.*

| Field | Value |
|---|---|
| Name | Personal data stays on-prem |
| Applies to | Global |
| Condition | `pii_detected && endpoint == "chat_completions"` |
| Send to | `ume/chat-onprem` |
| Fallbacks | none, on purpose |

![The new rule form with the pii_detected condition and the on-prem route as target](images/routing-pii-form.png)

Three things make this safe:

1. **Order matters: the first matching rule wins.** New rules are added last, so use **Check earlier** (the arrow buttons next to *Order*) until the
   rule is number 1. Otherwise a request with personal data that also matches *Premium via header* would go to `ume/chat-advanced`.
2. **No fallbacks.** If the on-prem model is down the call fails with `503 no_eligible_provider` or `502 all_providers_failed` instead of
   quietly sending the chat to an EU or external provider. Add a fallback only if it is also on-prem.
3. **Rules never widen access.** A key's allowed providers and residencies are applied after the rules, so the rule cannot send a chat anywhere the
   key may not go.

![The rule at the top of the list, checked first](images/routing-pii-rule.png)

Check the rule before relying on it with **Test a request**: ask for `ume/chat-standard`, open *Usage values* and set *Personal data* to
*Personal data found*:

![The Test a request dialog with "Personal data found" selected](images/routing-pii-test-input.png)

The result says which rule applies, which models the request is tried against and why each condition was true or false:

![The test result: Personal data stays on-prem applies, request goes to ume/chat-onprem](images/routing-pii-test-result.png)

On the real gateway (checked with the demo data and a synthetic personnummer): a chat containing `19121212-1212` was served by
`fake-onprem` (`x-ume-residency: OnPrem`, `x-ume-rule` set), while a chat without personal data went to the EU provider as before.

Notes:

- `pii_detected` is evaluated for every request that a rule asks about it, **whatever the key's PII policy** (Off, Allow, Redact), so the rule
  works even for keys that have no PII policy. If the key's policy is **Block**, the request is rejected before the rules run.
- If you only need "personal data stays on-prem" for specific keys, the key's PII policy **Reroute to on-prem** does the same without a rule.
  Use a rule when you want it per team or department, per model, with a chosen on-prem model, or combined with other conditions
  (for example `pii_detected && department_name == "Individ- och familjeomsorgen"`).
- Detection covers Swedish personnummer and samordningsnummer (with checksum), email addresses, Swedish phone numbers and IBANs (not bankgiro numbers).
  It is a safety net, not a guarantee that no personal data reaches a provider.

The same two examples are on the **Getting started** page of the admin UI:

![The Routing examples card on the Getting started page](images/getting-started-routing.png)

To regenerate the screenshots after a UI change, start the test stack (`.\scripts\Start-E2E.ps1`) and run
`npx playwright test -c playwright.docs.config.ts` in `tests\e2e`. The script builds the two rules through the UI, so it
changes the (disposable) test database only.

## How a rule works

| Field | Meaning |
|---|---|
| Name | Unique within its scope |
| Enabled | Disabled rules are validated but never evaluated |
| Scope | `VirtualKey`, `Team`, `Department` or `Global`; non-global rules also carry the id of the key/team/department |
| Priority | Lower is checked first within a scope (default 0) |
| Condition | An expression, see below. Empty = always matches |
| Targets | One or more model names (a route alias such as `ume/chat-advanced`, or a deployment such as `azure-swc/gpt-4o-mini`) with a relative weight (1-1,000,000) |
| Fallbacks | Further models tried, in order, after all targets |
| Chain | Route the rewritten model through the rules again |

### Evaluation order

1. Scopes are checked most specific first: key, team, department, global.
2. Within a scope, by ascending priority, then by name.
3. **The first rule whose condition is true wins.** Nothing else is evaluated.
4. If no rule matches, the request is routed exactly as before.

The matching rule's targets are put in weighted-random order (so a 70/30 split sends about 70 % of
requests to the first target), the remaining targets act as failover, and the fallbacks follow.
Duplicates are removed. The existing failover behaviour is unchanged: retryable provider failures
move to the next candidate, and nothing is retried once streaming has started.

### Chaining

With **Chain** on, the rule's result becomes the new model and all rules are checked again. Use it to
normalise names before routing, for example `gpt-4` to `ume/chat-standard` and then a tier rule on
`ume/chat-standard`. Chaining stops when no rule matches, a rule without Chain matches, the model
does not change, a model repeats, or after 8 steps. A rule is applied at most once per request. The
last matching rule decides the final models and fallbacks.

### Where rules run in a request

Authenticate, parse, then the requested model name is checked (known name: the key's model and provider
allow-lists and endpoint compatibility, as always). Then rate limit and the PII policy run. **Then the rules
run**, followed by candidate selection (provider allow-list, residency, PII restriction, circuit state,
streaming capability), budget reservation and the provider call with failover. A request that is rejected earlier
(bad key, rate limit, PII block) never reaches the rules.

The models a rule chose are tried in order: the first target's own alias targets (by priority and weight), then the
other targets, then the fallbacks. Providers whose circuit is open go last across all of them, so a healthy fallback
model is tried before a failing primary is retried.

**No silent return to the requested model.** If a rule matched and none of its models can serve the request (all
excluded by the key's restrictions, or the names do not exist or have the wrong kind), the request fails with
`503 no_eligible_provider`. To fall back to what the client asked for, add that model as an explicit fallback.

**Names only a rule knows.** A client may use a legacy name that does not exist in the catalogue if a rule rewrites
it (for example `gpt-4`). Such a request is checked against the key's model allow-list using the first model in the
rule chain that exists; if no rule matches, it is `404 model_not_found`, just later in the pipeline.

### Rules never widen access

Rules only choose among destinations. A key's allowed providers, allowed residencies, the PII
on-prem restriction, endpoint compatibility and disabled or drained providers are applied **after**
the rules, so a rule cannot send a request somewhere the key may not go. A key's model allow-list
applies to the model the client asks for.

## Managing rules in the admin UI

Open **Routing > Routing rules** (gateway-admin only).

- **List.** Rules are grouped by scope in the order the gateway checks them (key, team, department, global); within an owner
  they are numbered in checking order. A line under each name says where it sends traffic and flags anything unusual
  (*Chain*, *Disabled*, *Needs an owner*, *Ignored: invalid*). Filter by scope, or show only the rules that need an owner.
- **Detail.** The selected rule's condition, its targets with each one's share of the traffic, its fallbacks and its place in the
  order. You can edit, enable or disable, **move it earlier or later** (buttons, keyboard accessible), change its owner or scope,
  and delete it.
- **New rule / Edit.** Name, who it applies to (global, a department, a team or a key), the condition, the targets with weights, the
  fallbacks, **Chain** and **Enabled**. The condition is checked against the server as you type: a problem is shown with the character
  position and the offending text, and **Variables and examples** inserts variable names or complete examples. Model names are
  suggested from your routes and models.
- **Test a request.** Pick the model a client would ask for and, if you like, a key or team, headers, request parameters and usage values.
  The result says which rule applies, the models the request is tried against in order (and whether any do not exist), and for every
  rule that was checked which comparisons were true, false or could not be evaluated (for example a header that was not sent).
- **Deleting a team or department that has rules** (in **Organisation**) opens a question: **Deactivate** the rules (the recommended
  default; they wait under *Needs owner* until you assign a new team, department or key) or **Delete** them.
- **Usage** shows the rule that routed a request in the request details.

## Managing rules through the API

Only `gateway-admin` can manage rules in either place: they redirect traffic and spend. The admin API can create, edit, delete,
reorder, validate a condition while it is typed, and **dry-run** a sample request against the stored rules to see which
rule applies and, for every rule checked, which comparisons were true, false or impossible to evaluate (for example a
missing header). Saving validates the condition (with the character position of a problem), that targets and
fallbacks exist (a chained rule may point at a name another rule handles), that the owner exists and that the name is
unique in its scope. Global rules can be exported and imported with the rest of the configuration; scoped rules cannot,
because key/team/department ids differ between environments.

### When a team or department is removed, and when a key is rotated

Rules often belong to a service, not to the organisation chart: when a reorganisation removes a team or department
the same rules usually still apply, they just need a new owner. So deleting a team or department that has rules
**asks what to do** (the API answers 409 with the list of rules until the client sends `routingRules=delete` or
`routingRules=deactivate`):

- **Delete** removes the rules.
- **Deactivate** switches them off and keeps everything else. They show as *orphaned* (no owner) and cannot be
  enabled until they are **reassigned** to a new team, department or key (or made global), which enables them again.
  While they wait they can still be edited.

Both choices are saved together with the delete and written to the audit log for every rule. A rule scoped to a key
keeps applying to the key that replaces it in a rotation, in the same way budgets follow the rotation lineage.

### Names that rules use

A route alias or model that a rule uses as target or fallback cannot be deleted or renamed until the rule is changed.

## Condition language

Conditions use a subset of [CEL](https://cel.dev), implemented inside the gateway (no external
dependency). An expression is rejected when it is saved if it has a syntax error, an unknown variable,
a type error (such as comparing a number with text) or an invalid pattern, and the error points at the
position in the text.

### Variables

| Variable | Type | Description |
|---|---|---|
| `model` | text | The model name at this step (the requested name, or the rewritten one when chaining) |
| `endpoint` | text | `chat_completions`, `embeddings`, `responses` or `anthropic_messages` |
| `headers` | map of text | Request headers; names are case-insensitive: `headers["x-tier"]` |
| `params` | map | Top-level request fields that are text, numbers or booleans: `params["temperature"]` |
| `key_id`, `key_name` | text | The virtual key |
| `team_id`, `team_name` | text | Its team |
| `department_id`, `department_name` | text | Its förvaltning |
| `budget_used` | number | Percent (0-100) used of the budget closest to its limit among the key's, team's and förvaltning's budgets (so a nearly exhausted förvaltning budget counts even if the key's own budget is untouched). Includes requests currently in flight. Unset when no budget applies |
| `tokens_used` | number | Percent (0-100) of the key's tokens-per-minute limit used in the current minute. Unset when the key has no token limit |
| `pii_detected` | true/false | Whether the PII scan found personal data |
| `prompt_tokens` | number | Estimated input tokens |

### Operators and functions

`==  !=  <  <=  >  >=` · `&&  ||  !` · `in` (list or map key) · `+ - * / %` · `( )` · lists `[..]` ·
`m["key"]` and `m.key` · `s.startsWith(x)` `s.endsWith(x)` `s.contains(x)` `s.matches("regex")`
`size(x)` / `x.size()`. Text uses `"…"` or `'…'`; `r"…"` is a raw string (handy for regex).

Regular expressions must be written as a literal (not taken from a variable), are at most 256
characters and run on a non-backtracking engine, so a pattern cannot make evaluation slow.
Expressions are limited to 2,000 characters.

### Usage signals cost nothing unless used

`budget_used` and `tokens_used` are read-only lookups (one shared-counter read, no reservation, no effect
on limits). The gateway only performs them when at least one enabled rule mentions the variable, so
installations that do not use them pay nothing. A budget counter that is missing from the shared store
(cold start, new period) is rebuilt from recorded usage, exactly as budget enforcement does.

### Missing values

If a value does not exist (a header that was not sent, `budget_used` when no budget applies, a
type mismatch at runtime, division by zero) the comparison **does not match**, whichever way it is
written: `headers["x-nope"] != "a"` and `!(headers["x-nope"] == "a")` are both false. As in CEL,
`false && x` and `true || x` are decided even if `x` is missing.

### Examples

```text
headers["x-ume-tier"] == "premium"
budget_used > 90 && model == "ume/chat-standard"
team_name == "ml-research" && model.startsWith("claude-")
pii_detected && endpoint == "chat_completions"
model in ["gpt-4", "gpt-4-turbo"]
prompt_tokens > 20000 || params["max_tokens"] > 8000
```

## What you see

| Where | What |
|---|---|
| Response header `x-ume-rule` | Id(s) of the rule(s) that applied, comma-separated in chain order. Absent when no rule matched. `x-ume-model` still shows the deployment that served the request |
| Usage records | `RoutingRuleId` and `RoutingRuleName` of the last rule that applied (the name is stored so reports stay readable after a rename or delete); `RequestedModel` stays what the client asked for |
| Metric `ume.gateway.routing.rule_routed` | Count of rule-routed requests by endpoint (no per-rule label, to keep cardinality bounded) |
| Gateway log | A rule that fails validation is logged as a warning when the catalogue loads and is ignored |

### What conditions can and cannot see

`headers` contains the request headers **except** credentials (`authorization`, `proxy-authorization`,
`x-api-key`, `api-key`, `cookie`). `params` contains the top-level scalar fields of the JSON body (`temperature`,
`max_tokens`, `stream` …) **except** prompt content (`messages`, `input`, `prompt`, `system`, `instructions`,
`tools`, `contents`). Values are cut at 512 characters. Prompt content is never exposed to rules.

## Demo data

The seeded demo environment (see the README) contains three global rules:

| Rule | Condition | Effect |
|---|---|---|
| Äldre alias gpt-4 (chain) | `model == "gpt-4"` | Rewrites to `ume/chat-standard` |
| Premium via header | `headers["x-ume-tier"] == "premium" && model == "ume/chat-standard"` | `ume/chat-advanced`, falling back to `ume/chat-standard` |
| Budgeten nästan slut | `budget_used > 90 && model == "ume/chat-standard"` | `ume/chat-onprem` |

## Where this lives in the code

| Part | Location |
|---|---|
| Expression engine | `src/Ume.LlmGateway.Domain/Routing/Expressions/` |
| Rule evaluation | `src/Ume.LlmGateway.Domain/Routing/RoutingRuleSet.cs` |
| Rule storage | `RoutingRule` / `RoutingRuleTarget` entities, migration `RoutingRules` |
| Budget and token signals | `BudgetService.PeekUsedPercentAsync`, `IRateLimiter.PeekTokensUsedPercentAsync`; `RoutingRuleSet.References(variable)` tells the gateway which ones to fetch |
| Loading and caching | `GatewayCatalog` compiles the rules once per catalogue snapshot; invalid rules are skipped and logged as warnings |
| Admin API | `src/Ume.LlmGateway.AdminApi/RoutingRuleEndpoints.cs` |
| Request routing seam | `IRouteResolver` in `src/Ume.LlmGateway.Gateway/Pipeline/RouteResolver.cs` |
