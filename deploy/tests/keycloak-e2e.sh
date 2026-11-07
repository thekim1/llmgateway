#!/usr/bin/env bash
# End-to-end test of the optional Keycloak (deploy/keycloak, docs/keycloak.md), with Docker only:
#   - init-keycloak.sh against a stand-in gateway deployment (internal CA, secrets, deploy/.env);
#   - the gateway's Postgres image with postgres/init.sql and pg_hba.conf (TLS, role ume_keycloak, database keycloak);
#   - compose.yaml in production mode (optimized build, HTTPS, read-only file system) and the realm import;
#   - a second system's realm file in additions/: both systems' clients carry only their own roles in tokens, and
#     departmentCodes reaches the gateway's token;
#   - a second import changes nothing and deletes nothing another file or an administrator made;
#   - a dump of the keycloak database, restored after a change, brings Keycloak back as it was.
# Usage: deploy/tests/keycloak-e2e.sh            (about 3 minutes; cleans up after itself; KEEP=1 leaves it running)
set -euo pipefail

repo="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
id="kce2e$(openssl rand -hex 4)"; work="$(mktemp -d)"; net="${id}_data"; pg="${id}-postgres"
port="$((20000 + RANDOM % 20000))"
postgres_image="$(sed -n 's/^ *image: \(docker.io\/library\/postgres[^ ]*\)$/\1/p' "$repo/deploy/compose.prod.yaml" | head -1)"
fail() { echo "FAIL: $*" >&2; exit 1; }
step() { echo "== $*"; }

cleanup() {
  if [ -n "${KEEP:-}" ]; then echo "KEEP: left $id running, files in $work"; return; fi
  docker compose -p "$id" -f "$work/deploy/keycloak/compose.yaml" --env-file "$work/deploy/keycloak/.env" down -v >/dev/null 2>&1 || true
  docker rm -f "$pg" "${id}-ldap" >/dev/null 2>&1 || true
  docker network rm "$net" >/dev/null 2>&1 || true
  # Files the containers' users own (Postgres 70) cannot be removed as yourself.
  docker run --rm --user 0 --entrypoint rm -v "$work:/w" "$postgres_image" -rf /w/deploy /w/certs /w/secrets >/dev/null 2>&1 || true
  rm -rf "$work"
}
trap cleanup EXIT

step "stand-in gateway deployment in $work"
mkdir -p "$work/deploy" "$work/certs/postgres" "$work/secrets/postgres"
cp -r "$repo/deploy/keycloak" "$repo/deploy/postgres" "$work/deploy/"
rm -f "$work/deploy/keycloak/.env"
openssl req -x509 -newkey rsa:3072 -nodes -keyout "$work/certs/ca.key" -out "$work/certs/ca.pem" -days 30 -subj "/CN=test-ca" \
  -addext "basicConstraints=critical,CA:TRUE" -addext "keyUsage=critical,keyCertSign,cRLSign" >/dev/null 2>&1
openssl req -newkey rsa:3072 -nodes -keyout "$work/certs/postgres/postgres.key" -out "$work/pg.csr" -subj "/CN=postgres" >/dev/null 2>&1
printf 'subjectAltName=DNS:postgres\nextendedKeyUsage=serverAuth\n' > "$work/pg.ext"
openssl x509 -req -in "$work/pg.csr" -CA "$work/certs/ca.pem" -CAkey "$work/certs/ca.key" -CAcreateserial -CAserial "$work/ca.srl" \
  -out "$work/certs/postgres/postgres.crt" -days 30 -extfile "$work/pg.ext" >/dev/null 2>&1
cp "$work/certs/ca.pem" "$work/certs/postgres/ca.pem"
for role in bootstrap migrator gateway admin; do openssl rand -hex 16 > "$work/secrets/postgres/${role}_password"; done
cat > "$work/deploy/.env" <<EOF
UME_CERTS_DIR=$work/certs
UME_SECRETS_DIR=$work/secrets
UME_ADMIN_PUBLIC_HOST=admin.test.invalid
UME_ADMIN_BIND=127.0.0.1
UME_ADMIN_PORT=9443
EOF

step "init-keycloak.sh"
"$work/deploy/keycloak/init-keycloak.sh" --host "localhost:$port" --port "$port" --no-chown >/dev/null
grep -q "UME_ADMIN_PUBLIC_URL=https://admin.test.invalid:9443" "$work/deploy/keycloak/.env" || fail "admin URL not derived from deploy/.env"
[ -s "$work/secrets/adminapi/Oidc__ClientSecret" ] || fail "admin API client secret not created"
cmp -s <(tr -d '\n' < "$work/secrets/adminapi/Oidc__ClientSecret") "$work/secrets/keycloak-import/ume-admin-client-secret" || fail "client secrets differ"
first_secret="$(cat "$work/secrets/keycloak/db_password")"
"$work/deploy/keycloak/init-keycloak.sh" --no-chown >/dev/null
[ "$first_secret" = "$(cat "$work/secrets/keycloak/db_password")" ] || fail "a re-run replaced an existing secret"
# The containers run as other users (Postgres 70, Keycloak 1000, import 65534); in this throw-away copy, let them read.
chmod -R a+rX "$work/certs" "$work/secrets"
chmod 600 "$work/certs/postgres/postgres.key"
docker run --rm --user 0 --entrypoint chown -v "$work/certs/postgres:/c" "$postgres_image" 70:70 /c/postgres.key

step "Postgres with init.sql (TLS, ume_keycloak, database keycloak)"
docker network create --internal "$net" >/dev/null
# Labelled like a Compose service, so Backup-Database.ps1 and Restore-Database.ps1 find it.
docker run -d --name "$pg" --network "$net" --network-alias postgres --user 70:70 \
  --label "com.docker.compose.project=${id}gw" --label com.docker.compose.service=postgres \
  -e POSTGRES_PASSWORD_FILE=/run/postgres-secrets/bootstrap_password -e POSTGRES_INITDB_ARGS="--auth-host=scram-sha-256 --auth-local=trust" \
  -v "$work/certs/postgres:/run/certs:ro" -v "$work/secrets/postgres:/run/postgres-secrets:ro" \
  -v "$work/deploy/postgres/init.sql:/docker-entrypoint-initdb.d/10-roles.sql:ro" -v "$work/deploy/postgres/pg_hba.conf:/run/config/pg_hba.conf:ro" \
  "$postgres_image" postgres -c ssl=on -c ssl_cert_file=/run/certs/postgres.crt -c ssl_key_file=/run/certs/postgres.key \
  -c ssl_ca_file=/run/certs/ca.pem -c hba_file=/run/config/pg_hba.conf >/dev/null
pg_exec() { docker exec -i -e "PGPASSWORD=$(cat "$work/secrets/postgres/bootstrap_password")" "$pg" "$@"; }
# During initdb the image runs a temporary server on the Unix socket only, which pg_isready already reports as ready while
# init.sql is still running, then restarts. Wait for the final server (the only one listening on TCP) and the database.
pg_has_keycloak() { pg_exec psql -U postgres -tAc "SELECT datname FROM pg_database WHERE datname='keycloak'" 2>/dev/null | grep -qx keycloak; }
for _ in $(seq 60); do docker exec "$pg" pg_isready -h 127.0.0.1 -U postgres >/dev/null 2>&1 && pg_has_keycloak && break; sleep 1; done
pg_has_keycloak || fail "database keycloak missing"
# add-keycloak-database.sql is idempotent on an installation that already has them.
pg_exec psql -q -v ON_ERROR_STOP=1 -U postgres -d gatewaydb < "$work/deploy/postgres/add-keycloak-database.sql" >/dev/null \
  || fail "add-keycloak-database.sql failed on an existing database"

step "a test directory (OpenLDAP) with a user, a department attribute and a role group"
ldap_image="docker.io/osixia/openldap:1.5.0@sha256:18742e9c449c9c1afe129d3f2f3ee15fb34cc43e5f940a20f3399728f41d7c28"
ldap_admin_password="Ldap-$(openssl rand -hex 8)"; ldap_user_password="Lars-$(openssl rand -hex 8)"; local_password="Pilot-$(openssl rand -hex 8)"
mkdir -p "$work/ldif"
cat > "$work/ldif/50-test.ldif" <<EOF
dn: ou=people,dc=test,dc=invalid
objectClass: organizationalUnit
ou: people

dn: ou=roles,dc=test,dc=invalid
objectClass: organizationalUnit
ou: roles

dn: uid=lars.ldap,ou=people,dc=test,dc=invalid
objectClass: inetOrgPerson
uid: lars.ldap
cn: Lars Ldap
givenName: Lars
sn: Ldap
mail: lars.ldap@test.invalid
departmentNumber: 3000
userPassword: $ldap_user_password

dn: cn=department-admin,ou=roles,dc=test,dc=invalid
objectClass: groupOfNames
cn: department-admin
member: uid=lars.ldap,ou=people,dc=test,dc=invalid
EOF
chmod -R a+rX "$work/ldif"
docker run -d --name "${id}-ldap" --network "$net" --network-alias ldap \
  -e LDAP_ORGANISATION=Test -e LDAP_DOMAIN=test.invalid -e "LDAP_ADMIN_PASSWORD=$ldap_admin_password" -e LDAP_TLS=false \
  -v "$work/ldif:/container/service/slapd/assets/config/bootstrap/ldif/custom:ro" "$ldap_image" --copy-service >/dev/null
for _ in $(seq 60); do
  docker exec "${id}-ldap" ldapsearch -x -H ldap://localhost -b dc=test,dc=invalid -D cn=admin,dc=test,dc=invalid -w "$ldap_admin_password" uid=lars.ldap 2>/dev/null \
    | grep '^uid: lars.ldap' >/dev/null && break
  sleep 1
done
docker exec "${id}-ldap" ldapsearch -x -H ldap://localhost -b dc=test,dc=invalid -D cn=admin,dc=test,dc=invalid -w "$ldap_admin_password" uid=lars.ldap 2>/dev/null \
  | grep '^uid: lars.ldap' >/dev/null || fail "the test directory did not start"

step "Keycloak and the realm import (production mode)"
mkdir -p "$work/additions"
cat > "$work/additions/20-other.json" <<'EOF'
{
  "realm": "ume",
  "roles": { "realm": [ { "name": "other-admin" }, { "name": "other-viewer" } ] },
  "clients": [
    {
      "clientId": "other-app", "enabled": true, "protocol": "openid-connect", "publicClient": false,
      "secret": "$(env:OTHER_APP_SECRET)", "standardFlowEnabled": true, "directAccessGrantsEnabled": false, "fullScopeAllowed": false,
      "redirectUris": [ "https://other.test.invalid/signin-oidc" ],
      "protocolMappers": [ { "name": "roles", "protocol": "openid-connect", "protocolMapper": "oidc-usermodel-realm-role-mapper",
        "config": { "claim.name": "roles", "multivalued": "true", "jsonType.label": "String", "access.token.claim": "true", "id.token.claim": "true" } } ]
    }
  ],
  "scopeMappings": [ { "client": "other-app", "roles": [ "other-admin", "other-viewer" ] } ]
}
EOF
# A stand-in for an organisation's directory (Entra ID, AD FS): a second realm with a user in a group, and, in the
# shared realm, an OIDC identity provider for it whose mappers give brokered users roles and departmentCodes.
cat > "$work/additions/05-upstream-realm.json" <<'EOF2'
{
  "realm": "upstream", "enabled": true, "sslRequired": "external",
  "groups": [ { "name": "GG-Llm-Admins" } ],
  "clients": [
    {
      "clientId": "ume-broker", "enabled": true, "protocol": "openid-connect", "publicClient": false, "secret": "broker-secret",
      "standardFlowEnabled": true, "directAccessGrantsEnabled": false,
      "redirectUris": [ "$(env:KC_PUBLIC_URL)/realms/ume/broker/upstream/endpoint" ],
      "protocolMappers": [
        { "name": "groups", "protocol": "openid-connect", "protocolMapper": "oidc-group-membership-mapper",
          "config": { "claim.name": "groups", "full.path": "false", "id.token.claim": "true", "access.token.claim": "true", "userinfo.token.claim": "true" } },
        { "name": "department", "protocol": "openid-connect", "protocolMapper": "oidc-usermodel-attribute-mapper",
          "config": { "user.attribute": "department", "claim.name": "department", "multivalued": "true", "jsonType.label": "String",
            "id.token.claim": "true", "access.token.claim": "true", "userinfo.token.claim": "true" } }
      ]
    }
  ],
  "attributes": { "userProfileEnabled": "true" },
  "userProfile": { "unmanagedAttributePolicy": "ENABLED", "attributes": [
    { "name": "username", "permissions": { "view": ["admin", "user"], "edit": ["admin", "user"] } },
    { "name": "email", "permissions": { "view": ["admin", "user"], "edit": ["admin", "user"] } },
    { "name": "firstName", "permissions": { "view": ["admin", "user"], "edit": ["admin", "user"] } },
    { "name": "lastName", "permissions": { "view": ["admin", "user"], "edit": ["admin", "user"] } } ] },
  "users": [
    {
      "username": "anna.admin", "enabled": true, "email": "anna.admin@upstream.test.invalid", "emailVerified": true,
      "firstName": "Anna", "lastName": "Admin", "attributes": { "department": [ "2000" ] }, "groups": [ "GG-Llm-Admins" ],
      "credentials": [ { "type": "password", "value": "$(env:UPSTREAM_PASSWORD)", "temporary": false } ]
    }
  ]
}
EOF2
cat > "$work/additions/30-broker.json" <<'EOF2'
{
  "realm": "ume",
  "identityProviders": [
    {
      "alias": "upstream", "displayName": "Organisation (stand-in)", "providerId": "oidc", "enabled": true, "trustEmail": true,
      "firstBrokerLoginFlowAlias": "first broker login",
      "config": {
        "issuer": "$(env:KC_PUBLIC_URL)/realms/upstream",
        "authorizationUrl": "$(env:KC_PUBLIC_URL)/realms/upstream/protocol/openid-connect/auth",
        "tokenUrl": "https://keycloak:8443/realms/upstream/protocol/openid-connect/token",
        "jwksUrl": "https://keycloak:8443/realms/upstream/protocol/openid-connect/certs",
        "validateSignature": "true", "useJwksUrl": "true", "clientId": "ume-broker", "clientSecret": "broker-secret",
        "clientAuthMethod": "client_secret_post", "defaultScope": "openid email profile", "syncMode": "FORCE", "pkceEnabled": "true", "pkceMethod": "S256"
      }
    }
  ],
  "identityProviderMappers": [
    { "name": "admins", "identityProviderAlias": "upstream", "identityProviderMapper": "oidc-advanced-role-idp-mapper",
      "config": { "syncMode": "FORCE", "claims": "[{\"key\":\"groups\",\"value\":\"GG-Llm-Admins\"}]", "are.claim.values.regex": "false", "role": "gateway-admin" } },
    { "name": "department", "identityProviderAlias": "upstream", "identityProviderMapper": "oidc-user-attribute-idp-mapper",
      "config": { "syncMode": "FORCE", "claim": "department", "user.attribute": "departmentCodes" } }
  ]
}
EOF2
# Read-only LDAP user federation, as for Active Directory (vendor "other" here: the test directory is OpenLDAP).
cat > "$work/additions/25-ldap.json" <<'EOF2'
{
  "realm": "ume",
  "components": {
    "org.keycloak.storage.UserStorageProvider": [
      {
        "name": "directory", "providerId": "ldap",
        "config": {
          "enabled": ["true"], "priority": ["0"], "vendor": ["other"], "connectionUrl": ["ldap://ldap:389"], "authType": ["simple"],
          "bindDn": ["cn=admin,dc=test,dc=invalid"], "bindCredential": ["$(env:LDAP_BIND_PASSWORD)"],
          "usersDn": ["ou=people,dc=test,dc=invalid"], "userObjectClasses": ["inetOrgPerson"], "searchScope": ["1"],
          "usernameLDAPAttribute": ["uid"], "rdnLDAPAttribute": ["uid"], "uuidLDAPAttribute": ["entryUUID"],
          "editMode": ["READ_ONLY"], "importEnabled": ["true"], "syncRegistrations": ["false"], "trustEmail": ["true"], "pagination": ["false"]
        },
        "subComponents": {
          "org.keycloak.storage.ldap.mappers.LDAPStorageMapper": [
            { "name": "departmentCodes", "providerId": "user-attribute-ldap-mapper",
              "config": { "ldap.attribute": ["departmentNumber"], "user.model.attribute": ["departmentCodes"], "read.only": ["true"],
                "always.read.value.from.ldap": ["true"], "is.mandatory.in.ldap": ["false"], "is.binary.attribute": ["false"], "attribute.force.default": ["false"] } },
            { "name": "roles", "providerId": "role-ldap-mapper",
              "config": { "roles.dn": ["ou=roles,dc=test,dc=invalid"], "role.name.ldap.attribute": ["cn"], "role.object.classes": ["groupOfNames"],
                "membership.ldap.attribute": ["member"], "membership.attribute.type": ["DN"], "membership.user.ldap.attribute": ["uid"],
                "mode": ["READ_ONLY"], "user.roles.retrieve.strategy": ["LOAD_ROLES_BY_MEMBER_ATTRIBUTE"], "use.realm.roles.mapping": ["true"] } }
          ]
        }
      }
    ]
  }
}
EOF2
upstream_password="Up-$(openssl rand -hex 8)"
printf 'OTHER_APP_SECRET=other-secret\nUPSTREAM_PASSWORD=%s\nKC_PUBLIC_URL=https://localhost:%s\nLDAP_BIND_PASSWORD=%s\n' "$upstream_password" "$port" "$ldap_admin_password" > "$work/additions/import.env"
chmod -R a+rX "$work/additions"
cat >> "$work/deploy/keycloak/.env" <<EOF
UME_GATEWAY_DATA_NETWORK=$net
UME_KEYCLOAK_ADDITIONS_DIR=$work/additions
UME_KEYCLOAK_IMPORT_ENV=$work/additions/import.env
EOF
compose() { docker compose -p "$id" -f "$work/deploy/keycloak/compose.yaml" --env-file "$work/deploy/keycloak/.env" "$@"; }
compose up -d --build --wait keycloak >/dev/null 2>&1 || { compose logs keycloak | tail -40; fail "Keycloak did not become healthy"; }
compose run --rm realm-import > "$work/import1.log" 2>&1 || { tail -40 "$work/import1.log"; fail "realm import failed"; }

kc="https://localhost:$port"
curl_kc() { curl -sf --cacert "$work/certs/ca.pem" "$@"; }
token() {
  # Right after a (re)start Keycloak can report ready while it still answers "Bootstrap in progress": retry briefly.
  local i
  for i in $(seq 30); do token_once 2>/dev/null && return; sleep 1; done
  token_once
}
token_once() {
  curl -s --cacert "$work/certs/ca.pem" -d grant_type=client_credentials -d client_id=ume-realm-import -d "client_secret=$(cat "$work/secrets/keycloak/import_client_secret")" \
    "$kc/realms/master/protocol/openid-connect/token" \
    | python3 -I -c 'import json,sys; t=sys.stdin.read(); print(json.loads(t)["access_token"]) if "access_token" in t else sys.exit("no token: " + t)'
}
api() { local t; t="$(token)"; curl_kc -H "Authorization: Bearer $t" -H 'Content-Type: application/json' "$@"; }
json() { python3 -I -c "import json,sys; d=json.load(sys.stdin); print($1)"; }

step "realm checks"
[ "$(api "$kc/admin/realms/ume" | json 'd["bruteForceProtected"], d["passwordPolicy"].startswith("length(12)"), d["registrationAllowed"]')" = "True True False" ] \
  || fail "realm settings not applied"
api "$kc/admin/realms/ume/users/profile" | json '[a["name"] for a in d["attributes"]]' | grep -q "'teamCodes'" || fail "user profile lacks teamCodes"
admin_client="$(api "$kc/admin/realms/ume/clients?clientId=ume-admin" | json 'd[0]["id"]')"
other_client="$(api "$kc/admin/realms/ume/clients?clientId=other-app" | json 'd[0]["id"]')"
[ "$(api "$kc/admin/realms/ume/clients/$admin_client" | json 'd["fullScopeAllowed"], d["redirectUris"]')" = "False ['https://admin.test.invalid:9443/signin-oidc']" ] \
  || fail "ume-admin client wrong"
[ "$(api "$kc/admin/realms/ume/clients/$admin_client/client-secret" | json 'd["value"]')" = "$(cat "$work/secrets/keycloak-import/ume-admin-client-secret")" ] \
  || fail "ume-admin secret not the admin API's"
[ "$(api "$kc/admin/realms/ume/authentication/required-actions/CONFIGURE_TOTP" | json 'd["defaultAction"]')" = "False" ] || fail "OTP is enforced"

step "a user with roles of both systems: each token carries only its own system's roles"
api -X POST "$kc/admin/realms/ume/users" -d '{"username":"pilot.user","enabled":true,"email":"pilot.user@test.invalid","firstName":"Pilot","lastName":"User","attributes":{"departmentCodes":["1000"],"teamCodes":["T1"]}}' >/dev/null
user="$(api "$kc/admin/realms/ume/users?username=pilot.user&exact=true" | json 'd[0]["id"]')"
roles="$(api "$kc/admin/realms/ume/roles" | json 'json.dumps([r for r in d if r["name"] in ("gateway-admin","other-admin")])')"
api -X POST "$kc/admin/realms/ume/users/$user/role-mappings/realm" -d "$roles" >/dev/null
claims() { api "$kc/admin/realms/ume/clients/$1/evaluate-scopes/generate-example-access-token?scope=openid&userId=$user"; }
[ "$(claims "$admin_client" | json 'sorted(d.get("roles", [])), d.get("departmentCodes"), d.get("teamCodes")')" = "['gateway-admin'] ['1000'] ['T1']" ] \
  || fail "gateway token: $(claims "$admin_client")"
[ "$(claims "$other_client" | json 'sorted(d.get("roles", []))')" = "['other-admin']" ] || fail "other token: $(claims "$other_client")"

# Authorization code flow with PKCE as the admin API runs it, followed by hand with curl.
redirect="https://admin.test.invalid:9443/signin-oidc"
follow() { # follow URL [curl args]: follows redirects within Keycloak; prints the final URL, the page is in $work/page
  local next="$1"; shift
  for _ in $(seq 10); do
    case "$next" in "$redirect"*) echo "$next"; return;; esac
    next="$(curl -s --cacert "$work/certs/ca.pem" -b "$jar" -c "$jar" -o "$work/page" -w '%{redirect_url}' "$@" "$next")"
    set --
    [ -n "$next" ] || { echo "PAGE"; return; }
  done
}
signin() { # signin USER PASSWORD LOGIN-REALM [extra auth parameters]: prints the ID token's roles, departmentCodes and issuer
  local user="$1" password="$2" realm="$3" extra="${4:-}" verifier challenge url action final code id_token
  jar="$work/cookies-$user"; rm -f "$jar"; verifier="$(openssl rand -hex 32)"
  challenge="$(printf '%s' "$verifier" | openssl dgst -binary -sha256 | openssl base64 -A | tr '+/' '-_' | tr -d '=')"
  url="$kc/realms/ume/protocol/openid-connect/auth?client_id=ume-admin&response_type=code&scope=openid$extra&redirect_uri=$(python3 -I -c 'import sys,urllib.parse; print(urllib.parse.quote(sys.argv[1], safe=""))' "$redirect")&code_challenge=$challenge&code_challenge_method=S256&state=s1&nonce=n1"
  [ "$(follow "$url")" = PAGE ] || fail "$user: expected a login page"
  action="$(python3 -I -c 'import html,re,sys; print(html.unescape(re.search(r"action=\"([^\"]+)\"", open(sys.argv[1]).read()).group(1)))' "$work/page")"
  case "$action" in "$kc/realms/$realm/"*) ;; *) fail "$user: the login form is not realm $realm's: $action";; esac
  final="$(follow "$action" --data-urlencode "username=$user" --data-urlencode "password=$password")"
  case "$final" in "$redirect?"*code=*) ;; *) fail "$user: sign-in did not come back with a code: $final $(head -c 400 "$work/page")";; esac
  code="$(python3 -I -c 'import sys,urllib.parse; print(urllib.parse.parse_qs(urllib.parse.urlparse(sys.argv[1]).query)["code"][0])' "$final")"
  id_token="$(curl_kc -d grant_type=authorization_code -d client_id=ume-admin -d "client_secret=$(cat "$work/secrets/keycloak-import/ume-admin-client-secret")" \
    -d "code=$code" -d "redirect_uri=$redirect" -d "code_verifier=$verifier" "$kc/realms/ume/protocol/openid-connect/token" | json 'd["id_token"]')"
  python3 -I -c 'import base64,json,sys; p=sys.argv[1].split(".")[1]; d=json.loads(base64.urlsafe_b64decode(p+"="*(-len(p)%4))); print(sorted(d.get("roles",[])), d.get("departmentCodes"), d["iss"])' "$id_token"
}

step "brokered sign-in: an organisation's user gets the gateway's roles and departmentCodes"
claims="$(signin anna.admin "$upstream_password" upstream "&kc_idp_hint=upstream")"
[ "$claims" = "['gateway-admin'] ['2000'] $kc/realms/ume" ] || fail "brokered token: $claims"

step "LDAP federation: a directory user signs in with roles from LDAP groups and departmentCodes from an attribute"
claims="$(signin lars.ldap "$ldap_user_password" ume)"
[ "$claims" = "['department-admin'] ['3000'] $kc/realms/ume" ] || fail "LDAP token: $claims"

step "local account: a password sign-in with roles and departmentCodes set in Keycloak"
api -X PUT "$kc/admin/realms/ume/users/$user/reset-password" -d "{\"type\":\"password\",\"value\":\"$local_password\",\"temporary\":false}" >/dev/null
claims="$(signin pilot.user "$local_password" ume)"
[ "$claims" = "['gateway-admin'] ['1000'] $kc/realms/ume" ] || fail "local token: $claims"

step "a second import changes nothing and deletes nothing"
api -X POST "$kc/admin/realms/ume/clients" -d '{"clientId":"made-by-hand","enabled":true}' >/dev/null
compose run --rm realm-import > "$work/import2.log" 2>&1 || { tail -40 "$work/import2.log"; fail "second import failed"; }
api "$kc/admin/realms/ume/clients?clientId=made-by-hand" | json 'len(d)' | grep -qx 1 || fail "the import deleted a client made by hand"
api "$kc/admin/realms/ume/clients?clientId=other-app" | json 'len(d)' | grep -qx 1 || fail "the import deleted the other system's client"
api "$kc/admin/realms/ume/users?username=pilot.user&exact=true" | json 'len(d)' | grep -qx 1 || fail "the import deleted a user"

step "backup and restore of the keycloak database"
# With PowerShell, the operators' scripts; without, the same pg_dump/pg_restore calls.
if command -v pwsh >/dev/null; then
  pwsh -NoProfile -File "$repo/deploy/Backup-Database.ps1" -ComposeProject "${id}gw" -Database keycloak -Destination "$work/keycloak.dump" >/dev/null \
    || fail "Backup-Database.ps1 -Database keycloak failed"
else
  pg_exec pg_dump -U postgres -d keycloak -Fc -f /tmp/keycloak.dump
fi
api -X DELETE "$kc/admin/realms/ume/users/$user" >/dev/null
if command -v pwsh >/dev/null; then
  pwsh -NoProfile -File "$repo/deploy/Restore-Database.ps1" -ComposeProject "${id}gw" -Database keycloak -KeycloakProject "$id" \
    -Backup "$work/keycloak.dump" -ConfirmRestore >/dev/null 2>&1 && fail "restore ran while Keycloak was running"
fi
compose stop keycloak >/dev/null 2>&1
if command -v pwsh >/dev/null; then
  pwsh -NoProfile -File "$repo/deploy/Restore-Database.ps1" -ComposeProject "${id}gw" -Database keycloak -KeycloakProject "$id" \
    -Backup "$work/keycloak.dump" -ConfirmRestore >/dev/null || fail "Restore-Database.ps1 -Database keycloak failed"
else
  pg_exec pg_restore -U postgres -d keycloak --clean --if-exists --exit-on-error /tmp/keycloak.dump || fail "restore failed"
fi
compose up -d --wait keycloak >/dev/null 2>&1 || fail "Keycloak did not start after the restore"
api "$kc/admin/realms/ume/users?username=pilot.user&exact=true" | json 'len(d)' | grep -qx 1 || fail "restore did not bring the user back"
pg_exec psql -U postgres -d gatewaydb -tAc "SELECT 1" | grep -qx 1 || fail "gatewaydb affected"

step "hardening"
[ "$(docker inspect --format '{{.HostConfig.ReadonlyRootfs}} {{.HostConfig.CapDrop}}' "$(compose ps -q keycloak)")" = "true [ALL]" ] || fail "not hardened"
docker inspect --format '{{range .Config.Env}}{{println .}}{{end}}' "$(compose ps -q keycloak)" | grep -qiE '^KC_(DB_PASSWORD|BOOTSTRAP_ADMIN_PASSWORD|BOOTSTRAP_ADMIN_CLIENT_SECRET)=' \
  && fail "a secret is in the container's environment"

echo "PASS: Keycloak end-to-end"
