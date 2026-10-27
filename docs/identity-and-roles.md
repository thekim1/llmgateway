# Identity provider and roles

The admin UI and admin API sign people in through your existing OpenID Connect provider: Keycloak, AD FS or Entra ID.
The gateway itself never sees those users; applications call it with virtual keys. This page explains the access levels,
then how to set up each identity provider so the admin API gets the roles, departments and teams it needs.

The bundled Keycloak realm in `dev/keycloak` is for local runs only. In production, `UME_OIDC_AUTHORITY` in `deploy/.env`
points at your own provider. The settings below are also in the [runbook](runbook.md), *Identity provider*.

## Access levels

| Who | Signs in with | Can do |
|---|---|---|
| **Gateway admin** (`gateway-admin`) | The identity provider | Everything, in every department |
| **Department admin** (`department-admin`) | The identity provider | Manage teams, keys and budgets in their own departments |
| **Viewer** (`viewer`) | The identity provider | Read usage, PII incidents and the catalogue in their own departments |
| **Team admin** (`team-admin`) | The identity provider | Create, change, rotate and revoke the keys of their own teams |
| Signed in, no role | The identity provider | Only the catalogue; every other page and API call is refused with 403 |
| Application | A virtual key (`Authorization: Bearer`) | Call the gateway's `/v1` API within the key's policies |
| Integration (BI, SIEM) | A client-credentials token or a Data API key | Read the [Data API](data-access.md) with the permissions it was given |

The four admin roles come from the `roles` claim (or a group, see [Claim mapping](#claim-mapping)). There is no default
role. A user can have several roles; the highest one counts, except team admin, which adds its teams' keys to whatever
the other roles give.

### Department scope

Department admins and viewers only see the departments listed in their `departmentCodes` claim. Each value is matched
against a department's **cost-centre code** (*Organisation* in the admin UI), so the codes in the identity provider must
be the same as those in the gateway. A department admin or viewer with no codes sees no data at all. A gateway admin sees
every department whatever their codes are.

Scoping applies to departments and everything under them: teams, keys, budgets, alerts, usage, PII incidents and the CSV
export.

### Team scope

Team admins manage the keys of the teams listed in their `teamCodes` claim. Each value is matched exactly against a
team's **team code**, which a gateway admin or the department's admin sets on the team (*Organisation › Edit team*).
A team without a code has no team admins. Codes are unique across all teams.

A team admin can do everything with those keys that a department admin can: create them (the secret is shown once),
change every setting (models, residencies, PII policy, attachments, provider tools and storage, content guard, request
policy, limits, IP ranges, expiry), rotate and revoke them. They cannot reveal a stored secret, edit the team, set
budgets or read usage; the admin UI shows them **Keys** and the **Catalogue** only.

The team code alone decides: a team admin's department codes do not give them other teams' keys. A user who is both
viewer of a department and team admin of a team elsewhere reads the department's usage and manages only the team's keys.
A department admin who is also team admin manages the keys of their departments and of their teams, but edits only the
teams of their own departments.

### What each role can do

| Area | Gateway admin | Department admin | Viewer |
|---|---|---|---|
| Overview, usage, PII incidents, CSV export | All departments | Own departments | Own departments |
| Catalogue (models and base URL) | Yes | Yes | Yes |
| Teams | Create, edit, delete | Create, edit, delete in own departments | – |
| Departments | Create, edit, delete | See own | See own |
| Keys: create, edit, rotate, revoke | Yes | In own departments | – |
| Keys: reveal a stored secret (audited) | Yes | – | – |
| Keys: choose allowed providers | Yes | Left unchanged | – |
| Team codes (who are a team's team admins) | Yes | For teams in own departments | – |
| Budgets and alerts | Yes | For own departments, teams and keys | – |
| Providers, models, routes, routing rules | Yes | – | – |
| Settings, request logging | Yes | – | – |
| Operations (provider health, circuits) | Yes | – | – |
| Audit log | Yes | – | – |
| Data API access (clients and keys) | Yes | – | – |
| API reference (`/openapi`, `/scalar`) | Yes | – | – |

A **team admin** has a column of their own: keys of their own teams (create, edit, rotate, revoke; allowed providers
left unchanged, as for department admins) and the catalogue. Every other row is –.

Every change is written to the audit log with the user's `sub` claim (or name) as the actor.

### Sessions

A sign-in lasts 8 hours (a secure, HttpOnly cookie; no sliding expiry). The UI offers to extend it before it runs out,
but a session never lasts more than `Admin:MaxSessionHours` (12) from sign-in: then the user signs in at the identity
provider again. Roles, department codes and team codes are read at sign-in, so a role change in the identity provider applies at the
latest after that limit, or at once when the user signs out and in again. Sessions are stored on the server (Redis): signing
out ends the session, also for a copied cookie.

## What the admin API needs from the provider

Register one client (in the examples called `ume-admin`):

- **Flow**: authorization code with PKCE (S256). A client secret is optional; with one, pass it to `init-deployment.sh`
  with `--oidc-client-secret-file`, where it becomes the `Oidc__ClientSecret` secret file.
- **Redirect URI**: `https://<admin host>/signin-oidc`
- **Post-logout redirect URI**: `https://<admin host>/signout-callback-oidc`
- **Scopes**: `openid`, `profile` and `email` are always requested; add one more with `UME_OIDC_EXTRA_SCOPE`.
- **Sign-out**: the provider's discovery document must have an `end_session_endpoint`.

And these claims **in the ID token** (not only in the access token):

| Claim | Value |
|---|---|
| `roles` | `gateway-admin`, `department-admin`, `viewer` and/or `team-admin` |
| `departmentCodes` | The cost-centre codes the user may see; repeated claims, a JSON array or a single string |
| `teamCodes` | Team admins only: the team codes whose keys they manage; same formats |
| `name`, `email` | Shown in the UI |
| `sub` | The actor in the audit log |

### Claim mapping

If the provider cannot issue claims with exactly those names and values, map them in the gateway instead of changing the
provider. Set them in `deploy/.env` (for local runs, the same keys as `Oidc:<Setting>` in AppHost user secrets):

| Env variable | Setting | Default | Purpose |
|---|---|---|---|
| `UME_OIDC_ROLE_CLAIM` | `Oidc:RoleClaim` | `roles` | Claim holding the role names |
| `UME_OIDC_DEPARTMENT_CLAIM` | `Oidc:DepartmentClaim` | `departmentCodes` | Claim holding the department codes |
| `UME_OIDC_TEAM_CLAIM` | `Oidc:TeamClaim` | `teamCodes` | Claim holding the team codes; set it to the group claim to use group names as team codes |
| `UME_OIDC_GROUP_CLAIM` | `Oidc:GroupClaim` | `groups` | Claim holding group names |
| `UME_OIDC_GROUPS_GATEWAY_ADMIN` | `Oidc:RoleGroups:gateway-admin` | | Groups that grant the role, `;`-separated, case-insensitive |
| `UME_OIDC_GROUPS_DEPARTMENT_ADMIN` | `Oidc:RoleGroups:department-admin` | | As above |
| `UME_OIDC_GROUPS_VIEWER` | `Oidc:RoleGroups:viewer` | | As above |
| `UME_OIDC_GROUPS_TEAM_ADMIN` | `Oidc:RoleGroups:team-admin` | | As above |
| `UME_OIDC_EXTRA_SCOPE` | `Oidc:ExtraScopes:0` | `email` | One more scope to request, for example `groups` or `allatclaims` |

Group mapping means you do not need roles in the identity provider at all: put people in AD groups and list the groups.
For example, `UME_OIDC_GROUPS_GATEWAY_ADMIN=GG-Llm-Admins` and `UME_OIDC_GROUPS_VIEWER=GG-Llm-Readers;GG-Economy`.

**Teams from AD groups.** With one AD group per team, team codes need no attribute in the identity provider: set
`UME_OIDC_TEAM_CLAIM=groups` (the group claim), give each team its group name as team code (for example
`GG-Llm-Team-Kontaktcenter`) and list the groups whose members are team admins in `UME_OIDC_GROUPS_TEAM_ADMIN`, or
list the team groups themselves there to make every member a team admin. Every group then counts as a team code, but
only codes that a team has grant anything.

## Keycloak

Use a realm you already have (for example one federated to Active Directory). The development realm
`dev/keycloak/ume-realm.json` is a working example of every setting below.

1. **Client.** *Clients → Create client*: client ID `ume-admin`, OpenID Connect. Turn on *Standard flow* only. *Client
   authentication* on gives a confidential client (copy the secret from *Credentials*), off a public one. Under
   *Advanced → Proof Key for Code Exchange Code Challenge Method*, choose `S256`.
   Set *Valid redirect URIs* to `https://<admin host>/signin-oidc`, *Valid post logout redirect URIs* to
   `https://<admin host>/signout-callback-oidc` and *Web origins* to `https://<admin host>`.
2. **Roles.** Create realm roles `gateway-admin`, `department-admin`, `viewer` and `team-admin`, and give them to users or groups.
   With AD federation, the LDAP provider's *role-ldap-mapper* can create these roles straight from AD groups.
3. **Role claim.** In the client's dedicated scope (*Client scopes → ume-admin-dedicated*), add a *User Realm Role* mapper:
   token claim name `roles`, multivalued, *Add to ID token* on.
   Alternative: a *Group Membership* mapper (claim `groups`, *Full group path* off, *Add to ID token* on) plus
   `UME_OIDC_GROUPS_*`.
4. **Department codes.** Store them in a user attribute `departmentCodes`. In Keycloak 24 and later, first declare it under
   *Realm settings → User profile*, or Keycloak drops it. With AD, an LDAP *user-attribute-mapper* can fill it from an AD
   attribute that holds the cost-centre code. Then add a *User Attribute* mapper: user attribute and token claim name
   `departmentCodes`, multivalued, *Add to ID token* on.
   **Team codes** work the same way: a user attribute `teamCodes` (declared in the user profile) and a *User Attribute*
   mapper to the claim `teamCodes`. Or use groups, see [Claim mapping](#claim-mapping).
5. **Gateway.**
   ```bash
   UME_OIDC_AUTHORITY=https://keycloak.example.se/realms/<realm>
   UME_OIDC_CLIENT_ID=ume-admin
   ```

## Entra ID

1. **App registration.** *Entra ID → App registrations → New registration*, single tenant. Under *Authentication*, add the
   *Web* platform with both redirect URIs: `https://<admin host>/signin-oidc` and
   `https://<admin host>/signout-callback-oidc`. Under *Certificates & secrets*, create a client secret (the Web platform
   needs one).
2. **App roles.** Under *App roles*, create four roles with the values `gateway-admin`, `department-admin`, `viewer` and `team-admin`
   (allowed member types *Users/Groups*). Assign users or groups to them in *Enterprise applications → (the app) → Users
   and groups*; assigning groups needs Entra ID P1 or P2. The roles arrive in the `roles` claim of the ID token, so no
   mapping is needed. To make sure only assigned users can sign in, set *Assignment required?* to *Yes* on the enterprise
   application.
3. **Email.** Under *Token configuration*, add the optional claim `email` to the ID token.
4. **Department codes.** Entra has no ready-made claim for this. Put the cost-centre code in a user attribute (for example
   `extensionAttribute1`, synced from AD with Entra Connect), then in *Enterprise applications → (the app) → Single
   sign-on → Attributes & Claims* add a claim named `departmentCodes` with that attribute as source. For the claim to be
   issued without a custom signing key, set `"acceptMappedClaims": true` in the app registration's *Manifest*. If the
   claim gets another name, set `UME_OIDC_DEPARTMENT_CLAIM` to it.
5. **Gateway.**
   ```bash
   UME_OIDC_AUTHORITY=https://login.microsoftonline.com/<tenant id>/v2.0
   UME_OIDC_CLIENT_ID=<application (client) id>
   ```
   and the secret with `init-deployment.sh --oidc-client-secret-file <file>`.

Prefer app roles to the `groups` claim: Entra puts group object ids (not names) in it, and replaces the list with a link
when a user is in more than 200 groups. If you use groups anyway, list the object ids in `UME_OIDC_GROUPS_*`.

## AD FS

AD FS 2016 or later.

1. **Application group.** *AD FS Management → Application Groups → Add*: *Server application accessing a web API*. Name the
   server application `ume-admin` (its client identifier goes in `UME_OIDC_CLIENT_ID`), add the redirect URIs
   `https://<admin host>/signin-oidc` and `https://<admin host>/signout-callback-oidc`, and generate a shared secret. For
   the web API, use the client identifier as its identifier, so the ID token is issued for the same application.
2. **Permissions.** On the web API, permit the server application the scopes `openid`, `profile`, `email` and
   `allatclaims`. `allatclaims` copies the claims from the rules below into the ID token; request it with
   `UME_OIDC_EXTRA_SCOPE=allatclaims`.
3. **Issuance transform rules** on the web API:
   - *Send LDAP Attributes as Claims* (store *Active Directory*): `Token-Groups - Unqualified Names` → outgoing type
     `Group`, `E-Mail-Addresses` → `E-Mail Address`, `Display-Name` → `Name`, and the AD attribute that holds the
     cost-centre code (for example `departmentNumber`) → outgoing type `departmentCodes` (typed in by hand).
   - Or, instead of groups: *Send Group Membership as a Claim*, one rule per role: the AD group, outgoing type `Role`,
     value `gateway-admin` (and so on). Then set `UME_OIDC_ROLE_CLAIM=role` and leave the group settings empty.
4. **Gateway.**
   ```bash
   UME_OIDC_AUTHORITY=https://adfs.example.se/adfs
   UME_OIDC_CLIENT_ID=ume-admin
   UME_OIDC_EXTRA_SCOPE=allatclaims
   UME_OIDC_GROUP_CLAIM=group
   UME_OIDC_GROUPS_GATEWAY_ADMIN=GG-Llm-Admins
   UME_OIDC_GROUPS_DEPARTMENT_ADMIN=GG-Llm-DepartmentAdmins
   UME_OIDC_GROUPS_VIEWER=GG-Llm-Readers
   ```
   and the shared secret with `init-deployment.sh --oidc-client-secret-file <file>`. AD FS writes the group claim as
   `group`; with qualified names (`Token-Groups - Qualified by Domain Name`) the values look like `DOMAIN\GG-Llm-Admins`.

## Check the setup

Only the Keycloak defaults are tested end to end. The Entra ID and AD FS steps are the intended mapping; check them
against your own provider:

1. Sign in to the admin UI, then open `https://<admin host>/bff/user`. It shows the `roles` and `departmentCodes` the API
   received after mapping, without the tokens.
2. Sign in once as each role and check what you can see and change against the table above.
3. Sign in as a department admin and check that other departments' usage and keys are not visible.
4. Sign in as a team admin and check that only the keys of the teams with their team codes are listed. In local runs the
   Keycloak user `team-admin` manages the seeded team *Digitalisering & AI* (team code `digitalisering-ai`).
5. A user who can sign in but sees nothing has no role (or no department codes); an error on sign-in is usually a redirect
   URI or the authority URL. *Health and troubleshooting* in the [runbook](runbook.md#health-and-troubleshooting) has more.

**Local development against your provider.** Set `Oidc:Authority`, `Oidc:ClientId`, `Oidc:ClientSecret` and any mapping
settings in the AppHost user secrets. The AppHost then does not start the bundled Keycloak and passes the `Oidc:*`
settings to the admin API. Register the local admin callback URL (shown in the Aspire dashboard) at the provider.
