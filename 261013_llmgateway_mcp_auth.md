# MCP v2: per-user authorization, analysis and implementation plan

2026-10-13. Branch `mcp` (MCP v1 finished at `6ad043e`). Not scheduled; v1 ships without per-user authorization.

**Start condition.** v2 starts when the first personal MCP server is identified. The IdP that server trusts decides which
exchange flavour (D8) step B builds first, and whether it also needs the other. No personal server or target IdP is known
today, so the plan stays IdP-agnostic like the admin side (`Oidc`: Entra ID, Keycloak, AD FS). Where a step depends on the
IdP, it says so.

MCP v1 (ADR 017, [docs/mcp.md](docs/mcp.md)) authenticates clients with a team-owned virtual key and authenticates to each
upstream MCP server with one shared credential per server (`McpAuthType`: `None`, `StaticHeaders`,
`OAuthClientCredentials`; `src/Ume.LlmGateway.Infrastructure/Mcp/McpUpstreamAuth.cs`). Upstream servers never learn who the
user is, so personal tools ("my mail", "my cases", "my calendar") cannot be offered. This plan adds per-user authorization in
four steps:

- **A.** Users sign in to `/mcp` with the municipality's IdP (the gateway becomes an OAuth 2.1 protected resource).
- **B.** The gateway exchanges the user's token for one per upstream server (Entra On-Behalf-Of, RFC 8693).
- **C.** The same user identity reaches the gateway-run MCP tools on the LLM endpoints.
- **D.** (Later, outline only) third-party SaaS servers with stored per-user refresh tokens.

It needs ADR 018. Each step is sized for one subagent, as in v1.

---

## Part 1: Goals, non-goals and findings

### Goals

1. A person connects Claude Code, VS Code or Claude Desktop to `/mcp` by signing in with their municipal account. No key is
   pasted.
2. Calls to a "personal" MCP server carry a token that names the user and has that server as its audience, so the
   server applies the user's own permissions and logs the real user.
3. Budgets, grants, approval policies, PII policy, residency, rate limits and accounting still apply to signed-in users
   through the existing team and key model.
4. Machine clients keep using virtual keys, unchanged. A request with only a key behaves exactly as in v1.
5. The gateway never forwards a token it received (no token passthrough), and never stores a user token beyond its expiry.
6. User identifiers are minimised: pseudonymous in usage data, never in logs, metrics or traces.
7. Claude Desktop keeps using `mcp-remote` (an existing npm bridge, nothing to build), now with OAuth on the device (D3).

### Non-goals (v2)

- The gateway as an OAuth authorization server (its own `/authorize`, `/token`, DCR, consent page). The IdP stays the
  authorization server (D2).
- Per-user grants edited by admins. Access is granted per team (D5).
- Per-user budgets (decided). Key, team and department budgets apply unchanged; per-user rate limits (D6) cover fairness.
  `BudgetScope.User` is a v2.x idea only.
- Claude Desktop custom connectors reaching `/mcp` from Anthropic's cloud (v2.x, D3).
- Stored refresh tokens and "connect your account" flows for third-party SaaS (step D, v2.x).
- SSO for the admin UI changes nothing: the BFF (ADR 002) is untouched.
- Sending the user's identity to LLM providers (OpenAI `user`/`safety_identifier` and similar).

### Findings (verified 2026-10-13 unless marked)

**MCP specification (`2026-07-28`, authorization).**
- MCP servers MUST implement RFC 9728 Protected Resource Metadata (PRM). Clients MUST use it to find the authorization
  server, and MUST support both RFC 8414 and OpenID Connect Discovery on the authorization server.
- A `401` carries `WWW-Authenticate: Bearer resource_metadata="…"` and SHOULD carry `scope="…"`. Insufficient scope is `403`
  with `error="insufficient_scope"`.
- Clients MUST send RFC 8707 `resource` (the server's canonical URI) in authorization and token requests. Servers MUST
  validate that the token was issued for them, and "MUST NOT accept or transit any other tokens".
- Client registration priority: pre-registered client, then Client ID Metadata Documents (CIMD, when the authorization
  server advertises `client_id_metadata_document_supported`), then Dynamic Client Registration (DCR, now deprecated).
- The security best practices forbid token passthrough. They require per-client consent from an MCP proxy that runs its own
  OAuth flow towards a third party (confused deputy). That applies to us only if we become an authorization server (D2) or
  build step D.
- Authorization extensions live in `modelcontextprotocol/ext-auth`. **Enterprise-Managed Authorization** (ID-JAG) is
  marked stable there. In it the *client* exchanges the user's **ID token** at the enterprise IdP for an ID-JAG
  (`requested_token_type=urn:ietf:params:oauth:token-type:id-jag`). The client then redeems it at the MCP server's
  authorization server with the RFC 7523 `jwt-bearer` grant. See D9 for why it does not fit the gateway's upstream leg in v2.

**Clients.**
- **Claude Code:** RFC 9728 discovery; DCR or CIMD (its CIMD is `https://claude.ai/oauth/claude-code-client-metadata`);
  pre-registered client with `--client-id` (optional `--client-secret`) and `--callback-port`, or `oauth.clientId`,
  `oauth.callbackPort`, `oauth.scopes`, `oauth.authServerMetadataUrl` in `.mcp.json`. Redirect: loopback
  `http://localhost:<port>/callback`.
- **Claude Desktop, claude.ai, mobile, Cowork (custom connectors):** these connect **from Anthropic's cloud**, not from the
  user's device. Traffic comes from `160.79.104.0/21`. The MCP server *and* the IdP's discovery endpoints must be reachable
  from there; a server inside a private network needs **MCP tunnels** (Enterprise plan, research preview). Registration:
  Anthropic's CIMD (only when the authorization server advertises CIMD and `none` in
  `token_endpoint_auth_methods_supported`), DCR, or "Use your own OAuth client" (client id, optional secret). Redirect URI:
  `https://claude.ai/api/mcp/auth_callback`. A `401` is required to start sign-in, and only the first
  `authorization_servers` entry is used. With Entra, the MCP URL must be an Application ID URI on the Entra app, or the token
  request fails with `AADSTS9010010`.
- **VS Code:** `mcp.json` has `headers` and an `oauth` object (`clientId`, and `enterpriseManaged` (preview), which uses
  ID-JAG through the `mcp.enterpriseManagedAuth.idp` setting). Secondary sources say VS Code signs in to Entra-protected
  servers with its own built-in Entra client, found through PRM, and falls back to DCR otherwise. Not confirmed in VS Code's
  own docs: which client id it uses and whether the gateway app must list it in `preAuthorizedApplications` (open question T3).

**Identity providers.**
- **Entra ID:** no DCR. No evidence of CIMD support (searched; nothing found, so treat it as unsupported). On-Behalf-Of
  (OBO): `grant_type=urn:ietf:params:oauth:grant-type:jwt-bearer`, `requested_token_use=on_behalf_of`,
  `assertion` = the incoming token, `scope` = the downstream scope. The client authenticates with a secret, a certificate
  or a federated credential. The assertion's `aud` must be the app making the OBO request, and OBO works only for user
  principals (no app-only tokens). Delegated permissions are in `scp`, app roles in `roles`. Consent comes from admin
  consent, `knownClientApplications` or `preAuthorizedApplications`. Conditional Access failures return
  `interaction_required` with a `claims` challenge that the middle tier must pass back to the client. Loopback redirect URIs
  ignore the port. `http://` loopback redirects must be added in the manifest.
- **Keycloak:** standard token exchange (RFC 8693) has been supported since 26.2 (internal-to-internal: a client
  exchanges a token issued for itself for one with another audience or fewer scopes). It is switched on per client.
  Impersonation is not supported. DCR is supported. CIMD and ID-JAG issuance: secondary sources only (experimental or
  planned); the RFC 7523 JWT Authorization Grant is listed as supported in 26.6.

**SDK.** `ModelContextProtocol.AspNetCore` 2.2.0 (pinned) has `McpAuthenticationHandler`/`McpAuthenticationOptions`
(it serves PRM and adds `resource_metadata` to challenges) and the `ProtectedResourceMetadata` model.
`ModelContextProtocol.Core` 2.2.0 has client-side `ClientOAuthProvider`, DCR types and `IdentityAssertionGrantProvider`
(ID-JAG). There is no token-exchange (OBO or RFC 8693) helper for a server's outbound calls.

**OpenAI and Anthropic MCP fields.**
- OpenAI Responses `mcp` tool `authorization`: "an OAuth access token" for the remote MCP server. It is not stored and
  not shown in the Response, and must be sent on every request.
- Anthropic `mcp_servers[].authorization_token`: "OAuth authorization token if required by the MCP server". The caller
  runs the OAuth flow and refreshes the token.

Both are tokens **for the MCP server named in the tool**. When that server is a gateway slug, the right token is one whose
audience is the gateway (D11).

---

## Part 2: Design

### Decisions

**D1. The gateway is an OAuth 2.1 resource server for `/mcp` and, by configuration, for `/v1`. The IdP is the
authorization server.**
- One IdP issuer, configured like the admin side (`Oidc` in `AdminOptions.cs`; generic OIDC: Entra ID, Keycloak, AD FS).
- PRM points at that issuer. Tokens are JWTs validated locally: signature through JWKS (OIDC metadata cached by
  `ConfigurationManager`), exact issuer, audience in `UserAuth:Audiences`, lifetime, required scope.
- Entra-only checks: `tid` must equal the configured tenant; app-only tokens (no `scp`, `idtyp=app`) are refused.
- No database access on the request path.

**D2. Rejected: the gateway as an authorization server, or as a proxy in front of the IdP for DCR/CIMD.**
- It would make the gateway issue tokens and run a consent page, which brings the per-client consent, `state` and
  redirect-URI rules of the confused-deputy section.
- It would need its own signing keys and token storage. Bifrost does this; it is a large security surface.
- Pre-registered clients cover the three target clients against Entra (D4).
- Revisit only if many unknown clients must connect without an admin step (v2.x).

**D3. Claude Desktop stays on `mcp-remote` (decided). Custom connectors move to v2.x.**
- `mcp-remote` runs on the device: with a v1 key as today, and in v2 with OAuth on the device through a static client
  (`--static-oauth-client-info`; to be confirmed against the current `mcp-remote` README, T5).
- A possible later convenience: a packaged Claude Desktop extension (MCPB) that wraps `mcp-remote`, so users install one file
  instead of editing JSON. Unverified whether an extension can carry the OAuth setup.
- v2.x: Claude Desktop custom connectors connect from Anthropic's cloud, so `/mcp` would have to be reachable from
  `160.79.104.0/21`, through a public listener with an IP allowlist or MCP tunnels (Enterprise plan, preview). The tool
  results already go to Anthropic's model, but exposing `/mcp` at the edge is a separate network and DPIA decision.

**D4. Client registration: pre-registered public clients in Entra, DCR or pre-registration in Keycloak.**

The gateway itself needs nothing IdP-specific beyond configuration. The table covers the two IdPs documented first; AD FS
and others follow the same pattern (pre-registered clients where there is no DCR).

| Client | Entra ID | Keycloak |
|---|---|---|
| Claude Code | App "Ume LLM Gateway – Claude Code", public client, redirect `http://localhost/callback` (port ignored by Entra) and `http://127.0.0.1/callback` (manifest). Users run `claude mcp add --transport http --client-id <id> ume https://gw…/mcp`. | DCR (anonymous registration limited by client registration policies) or the same pre-registered client. CIMD only if the realm supports it (T4). |
| Claude Desktop (`mcp-remote`) | Same public client as Claude Code, given to `mcp-remote` as its static client (T5). | Same, or DCR. |
| VS Code | Built-in Entra sign-in (T3), otherwise `oauth.clientId` with a pre-registered client. | DCR or `oauth.clientId`. |
| Other / scripts | Virtual key, as today. | Virtual key. |

- The gateway's resource app ("Ume LLM Gateway") exposes the delegated scope `mcp.access`. Its Application ID URIs include
  `https://<gateway host>/mcp` (verified domain).
- The client apps hold delegated permission to `mcp.access`, with admin consent.
- `requestedAccessTokenVersion: 2`, so `aud` is the gateway app's client id and the issuer is the v2 issuer.

**D5. Users act through a team *user profile*: a virtual key of a new kind that cannot be presented (decided).**
- This is gateway-wide identity, not an MCP feature. Today a key's team decides budget, grants and policies; with SSO the team
  must come from the token. MCP v2 is only the first feature with personal sign-in, and a later SSO feature for the LLM
  endpoints reuses the same mapping (D11).
- The mapping uses an IdP app role or group (`ProfileClaim`). Entra app roles assigned to groups are recommended there, since
  they avoid the group overage limit.
- `VirtualKey.Kind = UserProfile`. It has no usable secret. It belongs to a team and holds everything a key holds today:
  MCP grants and approval policy, PII policy, allowed models, residencies and providers, rate limits, `McpCallsPerMinute`,
  and budgets at key scope.
- It lists the identity claim values that map users to it (`UserClaimValues`; Entra app role values or group object ids,
  Keycloak group names).
- The catalogue snapshot indexes claim value to profile keys. The authentication stage resolves a token to one profile
  key and then runs the unchanged pipeline with that key.
- If several profiles match: lowest `UserProfilePriority` wins, then the oldest. A client may pick another of its own
  matches with `x-ume-profile: <profile key id>`.
- Rejected alternatives:
  - **Per-user grants:** an admin burden, they hold personal data in configuration, and they duplicate what the upstream
    server enforces anyway.
  - **A synthetic key per user, created at first sign-in:** a database write on the request path, an unbounded number of
    keys, and grants that still have to come from somewhere.
  - **Grants directly on the team:** a second grant model next to keys, and every pipeline stage would need a second
    code path.

**D6. Rate limits on a profile key apply per user. Budgets on it are shared by the team's signed-in users.**
- The rate-limit, listen-stream and realtime-session keys use a *rate subject*: the key id for a standard key, and a
  deterministic GUID from (profile key id, user ref) for a profile key. `RedisKeys` signatures stay unchanged.
- Budgets keep their scopes (key, team, department). There are no per-user budgets in v2 (decided).

**D7. Machine keys can delegate for a user (`VirtualKey.AllowUserDelegation`, default off).**
- An application that holds a user's gateway-audience token (it ran its own sign-in) sends its key and the user's token
  together (D11). The key governs grants, policies and budgets; the user identity is used for token exchange and recorded
  for audit.
- A key without the flag that also sends a user token gets `403`.

**D8. Per-user upstream auth is a new `McpAuthType.TokenExchange = 3`, with two equal flavours.**
- Neither flavour is the chosen one. The first personal server's IdP decides which is built first (start condition); the
  second follows when a server needs it.
- `EntraObo`: OBO with the gateway's own Entra app as the confidential client. The incoming token's `aud` is the gateway
  app, as OBO requires.
- `Rfc8693`: Keycloak standard token exchange, or any RFC 8693 server, with the gateway's client.
- The client credential is gateway-wide (`UserAuth:Exchange`, from the secret store), because the subject token's audience
  must be that client. Per server, only the non-secret target is stored: Entra `scope` (for example
  `api://diarium/.default`); RFC 8693 `audience` and optional `scope` and `resource`.
- Rejected alternatives:
  - **Plain passthrough of the user's token:** forbidden by the MCP specification. The token's audience is the gateway, so
    every upstream would have to accept tokens for another resource.
  - **Asking clients for tokens per upstream:** clients would need to know every upstream's authorization server and run
    many sign-ins, and the gateway could not govern the calls.
  - **A shared credential plus a user header** (for example `X-User: oid`): the upstream would trust an unverifiable
    assertion.

**D9. ID-JAG (Enterprise-Managed Authorization) is not used for upstream calls in v2.**
- The flow is client-driven and needs the user's **ID token**, which a resource server never receives.
- Entra issuing ID-JAGs could not be confirmed. In Keycloak, issuing is not shipped according to secondary sources; only
  redeeming is.
- Revisit in v2.x on the inbound side: VS Code `enterpriseManaged` and Claude's Enterprise Managed Auth could reach the
  gateway without a consent screen if the gateway's authorization server (Keycloak 26.6+) accepts ID-JAG.

**D10. Exchanged tokens are cached in memory only, per instance, never in Redis.**
- Key: (server id, server credential fingerprint, user ref, hash of the subject token's `jti` or the token itself).
- Lifetime: `min(exchanged exp, subject exp) − 60 s`, and at most `Exchange:MaxCacheSeconds` (default 300, as in
  Bifrost). The cap means an IdP-side block or offboarding takes effect within minutes at the next exchange.
- Size-bounded, with one exchange at a time per key (single flight, as `McpTokenCache`).
- Rejected: a shared Redis cache, even encrypted. It would put bearer tokens for every personal system in a shared store,
  to save one IdP call per instance, user and server per hour. No `RedisKeys` or ACL change is needed.

**D11. User identity on the LLM endpoints.** A request has a *principal*: a key, a user (through a profile), or a key
delegating for a user. The token sources, in order:
1. `Authorization: Bearer <JWT>` (anything that is not `ume-sk-…`). A key may then come in `x-api-key`/`api-key`
   (delegation).
2. Responses `tools[].authorization` (type `mcp`) and Messages `mcp_servers[].authorization_token`. These were refused in
   v1 and are accepted in v2 **only** as a gateway-audience user token. They are validated exactly like (1) and never
   forwarded. All MCP tools in one request must name the same user, otherwise `400`.
3. A user token without a key on `/v1/*` (user-direct) is allowed only for `GatewayEndpoint` values in
   `UserAuth:Endpoints` (default `Mcp` and `McpToolCall`). Decided 2026-10-13: the LLM endpoints stay key-only by default.
   Applications hold keys and pass the user's token by delegation (D7); that is how the gateway and Bifrost already govern
   inference (Bifrost uses virtual keys for inference, SSO only for its dashboard and per-user credentials only for MCP).
   Turning on `Responses`, `Messages` or `ChatCompletions` for user-direct access stays possible per endpoint as an adopter
   decision, because it opens the whole LLM API to every mapped SSO user against shared team budgets.

**D12. Continuations stay bound to the user.**
- `resp_ume_` loop state and approvals are stored under the rate subject (key and user).
- The Data Protection purpose includes the user ref, so another user (or the bare key) cannot read or take the state.
- The state holds the user ref, never a token. A continuation must bring a valid token for the same user again, as OpenAI
  requires anyway.

**D13. Tool sync for per-user servers uses a separate sync identity.**
- `McpServer.SyncAuthType` (`None`, `StaticHeaders` or `OAuthClientCredentials`) and `EncryptedSyncCredential`. They are
  used only by the tool sync and the admin test, never by calls.
- Where the upstream offers no service identity, an admin imports a `tools/list` result (JSON from a test user), which goes
  through the same hashing and approval.
- Rejected: syncing with the admin's own token. The BFF keeps no tokens (ADR 002, `SaveTokens=false`), and it would tie
  the catalogue to one person's rights.
- **Tool lists that differ per user:** the approved catalogue is the upper bound. A user who may not use a tool gets the
  upstream's refusal as a tool error. A per-user `tools/list` filter (cached a few minutes in memory) is deferred to v2.x.

**D14. Users are recorded pseudonymously.**
- `UsageRecord.UserRef` = hex of HMAC-SHA256(`UserAuth:PseudonymKey`, issuer + `\n` + subject), 128 bits. The subject is
  `oid` for Entra (stable across apps; `sub` is pairwise) and `sub` for Keycloak.
- It is still personal data (pseudonymised, GDPR recital 26) under ADR 003's rules. Names, e-mail and UPN are never stored.
- Decided 2026-10-13: `UserRef` is kept 90 days (`UserAuth:UserRefRetentionDays`, default 90), long enough for incident
  and misuse investigation. After that a daily job clears the column; the usage row, its cost and its key and team stay,
  so budgets and reports are unaffected. The clearing is documented as gallring of that field, to be confirmed in the
  municipality's records retention decision (`docs/security-and-compliance.md`, which forbids silent deletion).
- Only gateway admins can look up usage by person, by entering the person's object id with a required reason. The admin
  API hashes the id, and the lookup and its reason are audited. Department admins and viewers see key and team totals only.
- Upstream servers see the real user. That is the point of step B, and their own logs are their records.

**D15. A missing or refused per-user authorization is a typed `mcp_auth_required` answer, not a bare error.**
- On `/mcp` it is JSON-RPC `-32005` with
  `data: { "type": "mcp_auth_required", "server": "<slug>", "reason": "<reason>", "message": "<Swedish text>" }`. Step D
  adds `"authorizeUrl"`.
- On REST it is `403` with `error.code = "mcp_auth_required"` and the same fields.
- In a gateway-run tool loop, the model gets the message as the tool's error output, so a plain chat client shows the user
  what to do (as Bifrost puts the URL in the message text).
- Reasons: `user_required`, `consent_required`, `interaction_required`, `user_not_allowed`, `upstream_denied`, and in
  step D `connect_required` and `reauth_required`.

### Compared with Bifrost

Bifrost (Maxim AI) has the upstream auth types `none`, `headers`, `oauth`, `per_user_headers`, `per_user_oauth` and
`token_exchange` (the last is enterprise-only). It offers lazy per-user authorization with an `mcp_auth_required` payload
and an `authorize_url`, and an *MCP Sessions* admin view of per-user credentials (re-authenticate, edit, revoke). Its
credential states include `needs_reauth` and `orphaned`. Clients reach its `/mcp` with a virtual key or by browser OAuth
against Bifrost acting as authorization server.

**Adopted:**
- Token exchange per call with a short in-memory cache and no stored credential (D8, D10, including Bifrost's 5-minute cap).
- The typed `mcp_auth_required` answer (D15).
- In step D: per-user headers as the cheap variant, a per-user credential view with revoke, and a `needs_reauth` state per
  credential. Like Bifrost, a refresh failure marks only that user's credential `needs_reauth` and only a permanent
  refusal does so; transient failures are retried.
- Bifrost's documented callback-URL caveat: in step D the callback URL is fixed configuration, and changing it is flagged.

**Done better (kept from v1, and they apply to signed-in users unchanged):**
- Tool definitions are pinned and approved; per-user servers are pinned through a sync identity or import (D13).
  Bifrost syncs per-user servers once with the admin's own OAuth.
- The PII guard on arguments and results, and residency per server.
- Per-call cost charged to budgets.
- Approval enforced by the gateway (the stricter of key and request).
- One stage for listing and calling.
- All of this is in the self-hosted, open product. Bifrost's token exchange, SSO, RBAC and audit logs are enterprise
  features.
- Revoke in step D also calls the provider's revocation endpoint; Bifrost's revoke only deletes its row.
- No bearer tokens in a shared store (D10).
- Pseudonymous user ids (D14).

**Deliberately skipped:**
- Bifrost as OAuth authorization server for inbound clients (D2).
- Anonymous or session-keyed identities (`x-bf-mcp-session-id`) and temporary-token links: every per-user credential here
  belongs to an IdP user.
- Credentials bound to a virtual key rather than a person.
- Per-user OAuth configured only by clicking through a test flow as the admin.

### Flow 1: discovery and sign-in on `/mcp` (Claude Code, Entra)

```
Client                         Gateway                               Entra ID
  | POST /mcp (no token)          |                                     |
  |------------------------------>| 401 WWW-Authenticate: Bearer        |
  |<------------------------------|   resource_metadata="https://gw/.well-known/oauth-protected-resource/mcp",
  |                               |   scope="api://ume-gw/mcp.access"   |
  | GET PRM                       |                                     |
  |------------------------------>| { resource: "https://gw/mcp",       |
  |<------------------------------|   authorization_servers: [issuer], scopes_supported: [...] }
  | GET issuer metadata (RFC 8414, then OIDC discovery)                 |
  |-------------------------------------------------------------------->|
  | pre-registered client id (--client-id), PKCE, resource=https://gw/mcp
  | browser: /authorize ... user signs in (MFA, Conditional Access)     |
  |-------------------------------------------------------------------->|
  | code -> /token (code_verifier, resource)                            |
  |<--------------------------------------------------------------------| access token: aud=gateway app,
  | POST /mcp  Authorization: Bearer <JWT>                              |   scp=mcp.access, oid, tid, roles
  |------------------------------>| validate JWT (JWKS cached) -> user ref
  |                               | roles/groups -> profile key (snapshot) -> pipeline as with a key
  |<------------------------------| result
```

### Flow 2: `tools/call` to a per-user server (token exchange)

```
Client           Gateway                                      IdP token endpoint          Upstream MCP (diarium)
  | tools/call      |                                                 |                          |
  |---------------->| auth stage: JWT -> principal(profile key, user) |                          |
  |                 | grant, residency, approval, rate (per user), schema, PII, budget   |       |
  |                 | server.AuthType == TokenExchange                |                          |
  |                 | cache (server, user, token) miss -> exchange    |                          |
  |                 |------------------------------------------------>| OBO / RFC 8693           |
  |                 |<------------------------------------------------| token aud=diarium, sub=user
  |                 | per-user upstream client: Authorization: Bearer <exchanged>                |
  |                 |--------------------------------------------------------------------------->|
  |                 |<---------------------------------------------------------------------------| result
  |                 | PII on result, accounting (UserRef), span (no user id)                     |
  |<----------------| result                                          |                          |
```

- Exchange errors are mapped, not passed through: see *Error mapping* in step B.
- The incoming token never leaves the gateway, except as the `assertion`/`subject_token` sent to the IdP that issued it.

### Flow 3: delegated user on `/v1/responses`

```
App (key with AllowUserDelegation) -> POST /v1/responses
  Authorization: Bearer ume-sk-…        (the key: budget, grants, PII)
  tools: [{ type: "mcp", server_label: "diarium", authorization: "<user JWT, aud=gateway>" }]
Gateway: validate JWT -> user; strip `authorization` before anything reaches a provider; run the loop;
  each tool call to a TokenExchange server exchanges as in flow 2; loop state is bound to (key, user).
```

### Data model and migration (`McpUserAuth`)

| Change | Notes |
|---|---|
| `VirtualKey.Kind` (`Standard = 0`, `UserProfile = 1`) | A profile key gets an unusable `KeyHash` (random, never shown) and `Prefix = "ume-up-…"`; `KeyAuthenticator` never returns it. |
| `VirtualKey.UserClaimValues` (`text[]`, default empty) | Profile keys only. Unique per value across active profiles *with the same priority* (validated in the service). |
| `VirtualKey.UserProfilePriority` (`int`, default 100) | Profile keys only. |
| `VirtualKey.AllowUserDelegation` (`bool`, default false) | Standard keys only. |
| `McpAuthType.TokenExchange = 3` | Its credential is the non-secret target JSON (`{"flavor","scope","audience","resource"}`), still stored through `CredentialProtector` for one code path. |
| `McpServer.SyncAuthType`, `McpServer.EncryptedSyncCredential` | D13. `SyncAuthType` cannot be `TokenExchange`. |
| `UsageRecord.UserRef` (`varchar(32)`, null) | Partial index `(UserRef, Timestamp) WHERE UserRef IS NOT NULL`. |
| `UsageRecord.Principal` (`smallint`: `Key`, `User`, `KeyForUser`) | For reports and the Data API. |
| `RequestOutcome.UserAuthorizationRequired` | Exchange refused for the user (consent, Conditional Access, not allowed). |
| `AuthFailureReason`: `InvalidUserToken`, `UserTokenExpired`, `UserNotMapped`, `DelegationNotAllowed` | Counted like key failures; the token is never kept. |
| `BlockedUser` (`UserRef` primary key, `CreatedAt`, `CreatedBy`, `Reason`) | Gateway-admin incident switch; in the snapshot. |

- DML grants for the new table go in the migrator.
- `DevSeeder`: one profile key "Demo users" mapped from the dev realm group `ume-users`, and the demo MCP server's
  per-user twin (`fakemcp_personal`, `TokenExchange`).
- Config export: profile keys are not exported (keys are not). The sync credential is masked like the existing credential.

### Configuration (`Gateway:UserAuth`, validated on start)

| Setting | Default | Notes |
|---|---|---|
| `Enabled` | `false` | Off = v1 behaviour, byte for byte. |
| `Authority` | — | Issuer URL. `[Required]` when enabled; https outside Development. |
| `MetadataAddress` | Authority + OIDC discovery | Override for IdPs with unusual paths. |
| `Audiences` | — | Accepted `aud` values. Entra: the gateway app's client id. Keycloak: the gateway client id. |
| `ResourceUri` | `Gateway:BaseUrl` + `/mcp` | The PRM `resource`. `/mcp/{slug}` gets `ResourceUri` + `/{slug}`. |
| `Scopes` | — | `scopes_supported`, the `scope` in `WWW-Authenticate`, and the scopes every token must carry (`scp`/`scope`). |
| `SubjectClaim` | `sub` | `oid` for Entra. |
| `TenantId` | null | Entra: required `tid`. |
| `ProfileClaim` | `roles` | `groups` for Keycloak. JSON-array claims are expanded as in `AdminAuthentication.ApplyClaimMapping`. |
| `Endpoints` | `[Mcp, McpToolCall]` | User-direct endpoints (D11). |
| `PseudonymKey` | — | Secret, 32+ bytes. Changing it breaks the link to older `UserRef`s (documented). |
| `Exchange:Flavor` | — | `EntraObo` or `Rfc8693`. |
| `Exchange:TokenUrl`, `ClientId`, `ClientSecret` | — | Secret store. A certificate or federated credential is T6. |
| `Exchange:CacheEntries` | 50 000 | In-memory cache size limit. |
| `Exchange:MaxCacheSeconds` | 300 | Upper bound on a cached exchanged token's life (D10). |
| `Exchange:TimeoutSeconds` | 10 | |
| `Mcp:UserClients:Max`, `IdleMinutes` | 2 000, 5 | Per-user upstream clients for handshake-era upstreams (step B). |

### Admin API and UI

- **Keys:**
  - The key form gets a *Type* (application key / user profile).
  - Profiles get *Claim values* and *Priority*; standard keys get *Allow user delegation*.
  - Profile keys have no reveal, rotate or "show once" actions.
  - The key list filters by type.
  - Endpoints: `POST /api/keys` takes `kind`; `PUT` keeps it immutable.
- **MCP servers:**
  - The auth type *Token exchange (per user)* with flavour, scope, audience and resource.
  - A *Sync identity* section.
  - `POST /api/mcp/servers/{id}/tools/import` (`tools/list` JSON).
  - The test button uses the sync identity.
  - Department admins may choose `TokenExchange` only for their own servers, like other settings.
- **Usage:**
  - A *Principal* filter (keys, users, delegated).
  - `GET /api/usage?user=<subject>` for gateway admins only. The API hashes the subject, writes the audit entry
    `usage.user_lookup` (subject masked) and never returns the subject.
- **Blocked users:** `GET/POST/DELETE /api/users/blocked` (gateway admin, audited); the UI is a small section on
  *Operations*.
- **Getting started:** OAuth snippets for Claude Code (`--client-id`), VS Code (`oauth.clientId`) and Claude Desktop
  (`mcp-remote` with a static client), shown when `UserAuth` is enabled. The admin API reads the public values
  (client ids, resource URI) from new `Gateway:PublicOAuth` settings.
- **Security:** `ApiFaultException` with Swedish messages; audit through `AuditSnapshots`; `OwnerScopeResolver` as today.

### Security and privacy

- **No passthrough.**
  - The incoming user token is sent only to its own issuer's token endpoint, as the OBO `assertion` or RFC 8693
    `subject_token`.
  - `Authorization` and the body fields `authorization`/`authorization_token` are removed before any provider request and
    are never in `ForwardHeaders` (`authorization` is already on the forbidden list in `GatewayRequestHandler.Stages.cs`).
  - Exchanged tokens are attached only to the upstream request for that server.
- **Audience binding:**
  - Inbound tokens must carry a configured audience and the required scope.
  - Exchanged tokens are requested for exactly one upstream target.
  - A token whose audience is an upstream is refused at the gateway (wrong audience).
- **Token storage:**
  - Tokens are held in memory only. The exchanged-token cache is bounded and expires at the earlier of the two expiries.
  - Tokens are never written to Redis, Postgres, logs, traces, loop state, audit or usage.
  - Token-bearing types follow the `McpUpstreamAuth` rule: no records, no `ToString`.
- **Revocation and limits:**
  - A disabled IdP account stops at the next exchange (Entra refuses OBO for a disabled user) and at token expiry for
    gateway-only use.
  - `BlockedUser` stops a person at once.
  - Disabling the profile key stops the whole team.
- **Session-free:** no `Mcp-Session-Id`. A per-user upstream client is never shared between users (step B), so an upstream
  session cannot carry one user's identity into another's call.
- **SSRF:** `Exchange:TokenUrl` and the OIDC metadata go through `McpAddressGuard` rules (https, no link-local or metadata
  addresses). They are fixed configuration, not admin-editable URLs.
- **GDPR (DPIA input changes, `docs/security-and-compliance.md`):**
  - New data flows: IdP claims to the gateway (in memory); the gateway to the IdP (exchange); the user's identity to
    upstream servers.
  - New stored identifier: `UserRef`, pseudonymous, cleared after 90 days (D14).
  - New recipients: none (upstreams are municipal systems already in the record of processing). Custom connectors through
    Anthropic's cloud are not part of v2 (D3).
  - Data subjects: staff who sign in. Purpose: authorization and accounting.
- **Data API:** `UserRef` is left out of feeds unless the consumer has the new permission `usage.user`. The minimum group
  size applies to per-user aggregates.
- **Logging:** `[LoggerMessage]` events carry `UserRef` at most, never the subject, name, UPN, e-mail or token. Metric labels
  and spans carry no user identifier.

### Observability

- **Metrics:**
  - `ume.gateway.user_auth` (counter; `result`: `ok`, `expired`, `invalid`, `wrong_audience`, `missing_scope`, `unmapped`,
    `blocked`, `delegation_denied`).
  - `ume.gateway.mcp.token_exchange` (counter by `server` and `outcome`; histogram `duration`).
  - `ume.gateway.mcp.token_cache` (hits and misses).
- **Spans:** `mcp.token_exchange` (server slug, flavour, outcome). The existing MCP spans get `ume.principal` (key, user,
  key_for_user).
- **Security events** (`Ume.LlmGateway.Security`, ADR 015):
  - `gateway.auth.user_token_rejected` (reason, endpoint, network).
  - `gateway.auth.user_not_mapped` (UserRef).
  - `gateway.mcp.user_auth_failed` (server, UserRef, reason).
  - `McpServerAuthFailed` is now also raised by the gateway when the *gateway's* exchange credential is refused
    (`invalid_client`). This closes the v1 deferral for that case. A per-user refusal does not raise it.
- **Health:** the OIDC metadata or JWKS being unreachable makes the `user-auth` health check `Degraded`, not `Unhealthy`, so
  keys keep working.
- **Performance:** the key-only path stays as it is (the token is classified by its `ume-sk-` prefix first). Benchmark
  `*Pipeline*` before and after step A. Budget for JWT validation: under 100 µs per request with cached keys.

---

## Part 3: Steps and checklists

Each step ends with `dotnet test --solution UmeLlmGateway.slnx`, the UI gate (see `HANDOVER.md`) when the UI changed, and a
`CHANGELOG.md` entry under `## Unreleased`.

**A. Inbound user identity on `/mcp`**

A1. Identity core (Gateway, Infrastructure)
- [ ] ADR 018 row in `docs/adr.md` (text in Part 4); scope row in `docs/architecture.md`.
- [ ] `UserAuthOptions` with validation; `UserTokenValidator` (`JsonWebTokenHandler`, `ConfigurationManager<OpenIdConnectConfiguration>`,
      issuer, audience, lifetime with 60 s skew, scopes, `tid`, app-only refusal, `alg` allow-list RS256/ES256/PS256).
- [ ] Principal resolution in `AuthenticateAsync`: a `ume-sk-` bearer or `x-api-key` gives a key; a JWT bearer gives a user;
      both give delegation. Returns `CallerPrincipal` (governing key, optional `UserIdentity` with the user ref and the raw
      token in memory). All call sites take the principal; key-only behaviour is unchanged.
- [ ] PRM endpoints `/.well-known/oauth-protected-resource/mcp` and `/.well-known/oauth-protected-resource/mcp/{slug}` (plus
      the root path for clients that probe it), built from the SDK's `ProtectedResourceMetadata` model. Served by the
      gateway itself, not through `McpAuthenticationHandler`, because the gateway runs its own authentication stage outside
      ASP.NET authentication. Cache headers: 1 hour.
- [ ] `401` on `/mcp` with `WWW-Authenticate: Bearer resource_metadata="…", scope="…"` (and `error="invalid_token"` for a bad
      or expired JWT). A JWT that is valid but not mapped gets `403` with a JSON error (no `insufficient_scope`, so clients
      do not loop). A JWT without the required scope gets `403 insufficient_scope`.
- [ ] Health check `user-auth`; metrics; security events.
- Tests (Gateway.Tests):
  - `TestJwtIssuer` in TestKit: an RSA key, OIDC metadata and JWKS served by WireMock, tokens with chosen claims.
  - Valid token; wrong issuer, audience, `tid` or algorithm; expired; not yet valid; missing scope; app-only token; JWKS key
    rotation (new `kid` refreshes the metadata).
  - PRM shape and paths; `401` header format; a key still works with `Enabled=false` and `true`; no token is logged
    (`CapturingLoggerProvider`).
  - Benchmark: the key path is unchanged.
- Docs: `docs/mcp.md` *Authentication*; `docs/runbook.md` (IdP metadata unreachable).

A2. Profiles, mapping and accounting (Domain, Infrastructure, Gateway, AdminApi)
- [ ] Migration `McpUserAuth` (the whole data-model table above, so B and C need no further migration); `DevSeeder`; dev realm
      (`dev/keycloak/ume-realm.json`): group `ume-users`, client `ume-gateway` (audience mapper, `groups` mapper,
      standard token exchange on), public client `ume-mcp-dev` (redirect `http://localhost/*`).
- [ ] `CatalogSnapshot`: index from claim value to profile keys; `BlockedUser` set.
- [ ] Profile resolution (priority, `x-ume-profile`), rate subject (D6) in the rate limiter, listen streams and realtime caps.
- [ ] `UsageRecord.UserRef`/`Principal` written by `AccountAsync`; auth failures with the new reasons.
- [ ] Daily job clearing `UserRef` older than `UserAuth:UserRefRetentionDays` (90); lookup by person requires a reason and is audited.
- [ ] Admin API: key kind, claim values, delegation flag, blocked users, usage by user (audited). `AuditSnapshots` for the
      new fields.
- [ ] Admin UI: key form and list, blocked users, usage *Principal* filter, Getting started OAuth snippets. Vitest.
- Tests:
  - Mapping with one, several (priority), none, a header choice that is not the user's own, a blocked user, an inactive
    team.
  - Per-user rate limits for two users on one profile.
  - Budget shared by the profile.
  - `UserRef` is stable and keyed; the subject is never stored.
  - Admin API: a lookup writes audit; department admins cannot look up users.
  - Integration (Testcontainers + WireMock issuer): sign-in token, then `/mcp` `tools/list` and `tools/call` on a v1 server
    with `UserRef` in the usage row.
- Docs: `docs/mcp.md` (*What a user sees*, profiles), `docs/admin-api.md`, `docs/security-and-compliance.md` (DPIA rows,
  technical controls), `docs/data-access.md` (`UserRef`, `usage.user`), `README.md`.

A3. Client setup (docs and manual checks only; written first for the IdP of the start condition)
- [ ] `docs/mcp.md` *Client setup*: the Entra app registrations (resource app, `mcp.access`, Application ID URIs, client apps,
      admin consent, manifest loopback redirect, `requestedAccessTokenVersion`) and the Keycloak setup (DCR policies or
      pre-registered client). Commands for Claude Code, VS Code and Claude Desktop (`mcp-remote` with a static client).
- [ ] Manual checks M1 to M4 (below).

**B. Per-user upstream tokens**

B1. Exchange core (Infrastructure, Gateway; IdP-dependent: build the flavour of the start condition first, the other when
a server needs it; the shared parts and tests do not depend on the flavour)
- [ ] `McpAuthType.TokenExchange` in `McpUpstreamAuth.Parse` (the target, no secret); `UserTokenExchanger` with the
      `EntraObo` and `Rfc8693` flavours, the shared `Exchange` client, timeout, and `McpAddressGuard` on the token URL.
- [ ] In-memory cache (D10) with single flight; lifetime = min of the two expiries − 60 s; never cached on failure.
- [ ] `McpUpstream`:
  - A server with `TokenExchange` gets its `Authorization` from the per-call context (the claimed request only), never from
    pooled state.
  - Against a `2026-07-28` upstream, the shared pooled client is used with the per-call token.
  - Against a handshake-era upstream, a client per (server, user ref) is used, LRU-capped and idle-evicted
    (`Mcp:UserClients`), with that user's token on `initialize` too.
- [ ] Visibility: a `TokenExchange` server is hidden from principals without a user (key-only), in lists and in calls (the
      same generic `-32602`, reason `user_required` in `gateway.mcp.tool_denied`). On `/mcp/{slug}` and in an LLM request that names the server explicitly, the answer is `mcp_auth_required` with `reason: "user_required"`, because the caller already knows the server exists.
- [ ] Error mapping:

  | Situation | `/mcp` answer | REST (`/v1/mcp/tools/call`) | Usage outcome | Event |
  |---|---|---|---|---|
  | Consent missing (Entra `invalid_grant` / AADSTS65001, RFC 8693 `invalid_scope`/`invalid_target`) | `mcp_auth_required` (D15), `reason: "consent_required"` | 403 `mcp_auth_required` | `UserAuthorizationRequired` | `gateway.mcp.user_auth_failed` |
  | Conditional Access (`interaction_required` with `claims`) | `mcp_auth_required`, `reason: "interaction_required"`; the claims challenge is not relayed in v2 (T2) | 403 | same | same |
  | User not allowed or disabled (`invalid_grant`, other) | `mcp_auth_required`, `reason: "user_not_allowed"` | 403 | same | same |
  | Gateway credential refused (`invalid_client`, `unauthorized_client`) | `-32003` | 502 `provider_error` | `ProviderError` | `McpServerAuthFailed` (server-level) |
  | IdP timeout or 5xx | `-32003` | 502 | `ProviderError` | — (counts toward the server circuit) |
  | Upstream 401/403 with an exchanged token | `mcp_auth_required`, `reason: "upstream_denied"` | 403 | `UserAuthorizationRequired` | `gateway.mcp.user_auth_failed`; no circuit count |

  Per-user failures never count toward the circuit breaker or mark the server `Degraded`.
- Tests (Gateway.Tests):
  - `TestJwtIssuer` gets a token endpoint for both flavours. It checks the client authentication, `assertion`/`subject_token`
    and target, and issues `aud = target, sub/oid = same user, act/azp = gateway`. It also has scripted errors.
  - `McpUpstreamServer` gets a mode that requires a bearer, checks issuer, audience and expiry, and records the subject per
    call.
  - Two users on one server get two tokens and two subjects upstream; the cache is hit within the lifetime and missed after
    expiry or a credential change; exchanges run single-flight under concurrency.
  - Every row of the error table.
  - A key-only principal cannot see the server.
  - The incoming token never reaches the upstream (the fake asserts its audience).
  - A handshake-era upstream gets one client per user, with eviction.
  - No token in logs.
- Integration: a Keycloak Testcontainer (≥ 26.2, realm from A2) does a real RFC 8693 exchange against the fake upstream.
  Pin the image digest, as `TestPostgres` does.
- Docs: `docs/mcp.md` (*Per-user servers*, error codes), `docs/runbook.md` (consent and exchange failures, Entra admin
  consent), `docs/security-and-compliance.md`.

B2. Sync identity and import (AdminApi, UI)
- [ ] `SyncAuthType`/`EncryptedSyncCredential` in `McpToolSyncService` and the test endpoint; `tools/import`.
- [ ] Admin UI: auth type *Token exchange* (fields per flavour), *Sync identity*, *Import tool list*.
- [ ] `ConfigTransferService`: the new fields, with the sync credential masked.
- Tests: sync with a sync identity; import reaches the same `Pending`/`Changed` states and hashes as a sync; a
  `TokenExchange` server without a sync identity reports "import needed" and does not fail. Vitest for the form.
- Docs: `docs/mcp.md` *Tool sync and pinning*, `docs/admin-api.md`.

**C. LLM endpoints**

- [ ] User-direct authentication on the endpoints in `UserAuth:Endpoints` (D11), through the shared principal stage.
- [ ] `ResponsesMcpTool.RefusedMembers`: drop `authorization` (keep `headers`, `connector_id`).
      `AnthropicMcpServer`: accept `authorization_token`. Both are validated as user tokens; all MCP tools in a request must
      name the same user; a token combined with a key that does not allow delegation gets 403; the fields are removed
      before the provider call.
- [ ] `McpLoopStates`: the rate subject and user ref in the storage key and the Data Protection purpose (D12). A
      continuation with another user or none is "not found".
- [ ] `/v1/mcp/tools` and `/v1/mcp/tools/call`: user-direct and delegated (key in `x-api-key`, user in `Authorization`).
- [ ] Usage: every turn and tool call carries the same `UserRef` and `Principal`.
- Tests:
  - Delegated and user-direct runs of the hosted `mcp` tool (streamed and not) and the `/v1/messages` connector against a
    `TokenExchange` server.
  - Mixed users in one request are refused.
  - The token never appears in the provider request (WireMock body assertion).
  - Approval continuation by the same user works; another user, a bare key or an expired token is refused.
  - `UserAuth:Endpoints` gating.
  - No `UserRef` reaches the provider (no `user`/`safety_identifier` set).
- Docs: `docs/mcp.md` (*Using MCP tools with the LLM APIs*, *Hosted MCP tools*, *MCP connector*: the fields now accepted
  and what they must contain), `docs/admin-api.md` if anything changed there.

**D. Third-party SaaS servers (v2.x, optional; outline only)**

Scope: GitHub, Atlassian, M365 in other tenants, and other services whose authorization server trusts neither the municipal
IdP nor token exchange.
- A new `McpAuthType.UserOAuth`. Per (server, user ref), the gateway runs the authorization code flow with PKCE as the
  client, with its own registered client at the SaaS.
- A cheaper variant first: `McpAuthType.UserHeaders`. Each user submits their own API key or personal access token on the
  page below; it is stored encrypted per (server, user ref) and verified upstream before it is saved. Values are never
  shown again (as Bifrost's `per_user_headers`).
- Each stored credential has a state: `Active`, `NeedsReauth` (the refresh was permanently refused, or upstream answers
  401) or `NeedsUpdate` (headers). It is sticky until the user reconnects; transient refresh failures are retried
  silently.
- The refresh token is stored encrypted (Data Protection, purpose bound to server and user) in a new table, never in
  Redis. The access token is held in memory as in D10.
- A "Connect your account" page:
  - Either a small user-facing page on the gateway behind the same OIDC sign-in, or in the admin UI for all signed-in
    users. The admin UI is admin-only today (ADR 002), so a user-facing page is the likely choice.
  - A missing connection answers `mcp_auth_required` (D15) with `reason: "connect_required"` or `"reauth_required"` and
    an `authorizeUrl`. The URL is bound to the signed-in user (only that user can complete it) and expires after 10
    minutes.
  - It shows the user's connected accounts, their scopes, state and last use, and offers reconnect and disconnect.
  - An admin view (*MCP > User connections*, gateway admins and the server's department admins) lists connections per
    server by `UserRef`, state and expiry (never values), and can revoke them. Revocation is audited.
- The callback URL is fixed configuration (`UserOAuth:CallbackUrl`). Changing it breaks every registered SaaS client
  (redirect URI mismatch, as Bifrost documents), so the admin UI warns and lists the clients to re-register.
- Revocation: disconnect, and admin revoke, call the SaaS revocation endpoint (RFC 7009) where one exists, then delete the row. The row is deleted when the
  user is blocked or their profile no longer maps, and after `UserOAuth:IdleDays`.
- Confused-deputy rules apply here in full: a consent page owned by the gateway and bound to the client, `state` stored only
  after consent, exact redirect match, `frame-ancestors 'none'`.

Why defer:
- It makes the gateway a long-term holder of personal credentials to external systems. That needs its own DPIA, and transfer
  questions for US SaaS.
- It is the largest security surface of the four steps, and it needs a user-facing UI the project does not have.
- No municipal use case has been named yet (start condition).

### Manual checks (need a human)

The Entra checks (M1, M2, M5) apply when the first server's IdP is Entra; M4 when it is Keycloak.

- M1. Real Entra tenant:
  - App registrations as in A3.
  - Claude Code signs in with `--client-id` and calls a tool. Check the token's `aud`, `scp`, `tid` and `oid` in the gateway
    debug view (Development only, no token output).
  - Conditional Access with MFA works.
  - A user without the app role gets `403`.
- M2. VS Code against the same tenant: built-in Entra sign-in or `oauth.clientId` (resolves T3).
- M3. Claude Desktop through `mcp-remote` with a static OAuth client: sign-in on the device, tools work (resolves T5).
- M4. Keycloak (dev realm and the adopter's realm): Claude Code with DCR; Claude Code with a pre-registered client.
- M5. OBO to a real Entra-protected API behind an MCP server (for example a test MCP server that calls Graph `/me`): consent
  missing, admin consent granted, Conditional Access challenge, disabled user.
- M6. Python examples in `docs/mcp.md` for delegated `/v1/responses` with a real user token.

### Open questions for the product owner

Decided 2026-10-13: Claude Desktop stays on `mcp-remote` (D3); user to team through IdP role or group (D5); no per-user
budgets (D6). Which personal servers and which IdP are unknown and form the start condition at the top.

Also decided 2026-10-13: `UserRef` kept 90 days, lookup by gateway admins only with an audited reason (D14); LLM
endpoints key-only by default, users reach them through applications with delegating keys (D11). No product-owner
questions remain open; the municipality's records retention decision must still confirm the 90-day clearing.

### Open technical questions (unverified)

- T1. Whether Entra's discovery document satisfies every client's PKCE check (`code_challenge_methods_supported`). Check
  in M1 and M2. Fallback for Claude Code: `oauth.authServerMetadataUrl`.
- T2. Relaying Entra Conditional Access `claims` challenges to MCP clients. No MCP client is known to handle them; v2
  reports `interaction_required`, and the user signs in again with the stricter policy.
- T3. The VS Code built-in Entra client id, and whether the resource app must pre-authorize it.
- T4. Keycloak CIMD support (secondary sources say experimental) and RFC 8707 `resource` handling (an audience mapper is
  the fallback).
- T5. `mcp-remote` options for a static OAuth client in its current release.
- T6. Certificate or federated credential for the OBO client instead of a secret (Entra recommends it; the deployment
  certificate store is the open part).
- T7. Whether Entra issues `aud` as the client id for every Application ID URI used as `resource` (expected with
  `requestedAccessTokenVersion: 2`), and how many per-slug URLs can be Application ID URIs. Proposal: document the aggregated
  `/mcp` for OAuth clients and add slug URLs on request.

---

## Part 4: Proposed ADR 018 (add to `docs/adr.md` in step A1)

| 018 | MCP users sign in with the municipal IdP; the gateway exchanges their token per upstream and never passes it through |
Signed-in users are an addition to virtual keys, not a replacement:
- The gateway is an OAuth 2.1 protected resource (RFC 9728 metadata, RFC 8707 audience) for `/mcp` and, by configuration,
  for `/v1`.
- The municipal IdP stays the authorization server. Clients are pre-registered in Entra (no DCR there) or registered by DCR
  in Keycloak. The gateway runs no authorization server, consent page or token store of its own.
- A user acts through a team *user profile* (a virtual key that cannot be presented), chosen from IdP roles or groups held
  in the catalogue snapshot. All existing policies, grants and budgets therefore apply unchanged; profile rate limits apply
  per user.
- Applications may delegate for a user only with a key that allows it.
- Per-user upstream servers get a token exchanged for them (Entra On-Behalf-Of or RFC 8693). Exchanged tokens are cached in
  memory until expiry, never shared or stored. The caller's token is never forwarded (MCP forbids token passthrough).
- Per-user servers are synced with a separate service identity or an imported tool list.
- Users are recorded as a keyed pseudonym (`UserRef`), never by name, and never in logs or telemetry.
- ID-JAG, per-user budgets and stored third-party refresh tokens are deferred.

---

## Sources

- MCP authorization (`2026-07-28`): https://modelcontextprotocol.io/specification/2026-07-28/basic/authorization
- MCP client registration: https://modelcontextprotocol.io/specification/2026-07-28/basic/authorization/client-registration
- MCP security best practices: https://modelcontextprotocol.io/specification/2026-07-28/basic/security_best_practices
- MCP authorization extensions: https://github.com/modelcontextprotocol/ext-auth and
  https://github.com/modelcontextprotocol/ext-auth/blob/main/specification/stable/enterprise-managed-authorization.mdx
- Claude Code MCP (OAuth, `--client-id`, `--callback-port`, `oauth` keys): https://code.claude.com/docs/en/mcp
- Claude custom connectors: https://claude.com/docs/connectors/custom/remote-mcp
- Claude connector authentication (CIMD/DCR, callback URLs, Entra note, egress range): https://claude.com/docs/connectors/building/authentication
- MCP tunnels: https://claude.com/docs/connectors/mcp-tunnels/overview
- VS Code MCP configuration (`oauth.clientId`, `enterpriseManaged`): https://code.visualstudio.com/docs/agents/reference/mcp-configuration
- VS Code and Entra (secondary): https://den.dev/blog/vscode-authorization-mcp/
- Entra On-Behalf-Of: https://learn.microsoft.com/en-us/entra/identity-platform/v2-oauth2-on-behalf-of-flow
- Entra redirect URIs (loopback port ignored): https://learn.microsoft.com/en-us/entra/identity-platform/reply-url
- Keycloak standard token exchange: https://www.keycloak.org/2025/05/standard-token-exchange-kc-26-2
- Keycloak JWT Authorization Grant (26.5, 26.6): https://www.keycloak.org/2026/01/jwt-authorization-grant,
  https://www.keycloak.org/2026/04/keycloak-2660-released
- Keycloak and ID-JAG (secondary): https://skycloak.io/blog/cross-app-access-id-jag-ai-agents-keycloak/
- OpenAI Responses MCP tool (`authorization`): https://developers.openai.com/api/docs/guides/tools-connectors-mcp
- Anthropic MCP connector (`authorization_token`): https://platform.claude.com/docs/en/agents-and-tools/mcp-connector
- Bifrost MCP: https://docs.getbifrost.ai/mcp/overview, https://docs.getbifrost.ai/mcp/auth/overview,
  https://docs.getbifrost.ai/mcp/auth/per-user-oauth, https://docs.getbifrost.ai/mcp/sessions, https://docs.getbifrost.ai/mcp/gateway-auth
- C# MCP SDK 2.2.0: package XML docs (`McpAuthenticationHandler`, `ProtectedResourceMetadata`, `IdentityAssertionGrantProvider`).

Not verified (open questions above): Entra CIMD or ID-JAG support (no source found), the VS Code Entra client id, Keycloak
CIMD, and the current `mcp-remote` static-client options.
