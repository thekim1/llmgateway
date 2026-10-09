#!/usr/bin/env bash
# First-run setup for deploy/compose.prod.yaml: generates an internal CA, service
# certificates, the Data Protection certificate, database/Redis passwords, the key
# pepper and deploy/.env. Safe to re-run: existing secrets are never overwritten
# (regenerating the pepper or Data Protection certificate would break existing data).
#
#   ./deploy/init-deployment.sh                      # prompts for what it needs
#   ./deploy/init-deployment.sh --oidc-authority https://idp.example.se/realms/ume \
#       --gateway-host llm.example.se --admin-host llm-admin.example.se --yes
#   ./deploy/init-deployment.sh --renew-certs        # re-issue internal certificates only
#
# Exposure options (see docs/runbook.md, "Reverse proxy and PKI"):
#   --proxy                 a reverse proxy (nginx, Nginx Proxy Manager, F5...) terminates public
#                           TLS on 443 and re-encrypts to this host; needs distinct hostnames
#   --upstream-host HOST    address the proxy uses to reach this Docker host (default: hostname)
#   --oidc-client-secret-file FILE
#                           confidential OIDC client: install the secret for the admin API
#                           (omit for a public client using PKCE only)
#   --gateway-cert/--gateway-key/--admin-cert/--admin-key/--ca-file FILE
#                           use certificates from your PKI for the public listeners instead of
#                           the generated internal CA (ca-file = your root/intermediate chain)
set -euo pipefail
umask 077

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
certs="$here/certs"; secrets="$here/secrets"; env_file="$here/.env"
oidc_authority=""; oidc_client="ume-admin"; gateway_host=""; admin_host=""
gateway_port=8443; admin_port=9443; bind=""; yes=0; renew=0
oidc_secret_file=""; proxy=0; upstream_host=""; gw_cert=""; gw_key=""; adm_cert=""; adm_key=""; ca_file=""

usage() { sed -n '2,23p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'; exit "${1:-0}"; }
die() { echo "error: $*" >&2; exit 1; }

while [ $# -gt 0 ]; do
  case "$1" in
    --oidc-authority) oidc_authority="$2"; shift 2;;
    --oidc-client-id) oidc_client="$2"; shift 2;;
    --gateway-host) gateway_host="$2"; shift 2;;
    --admin-host) admin_host="$2"; shift 2;;
    --gateway-port) gateway_port="$2"; shift 2;;
    --admin-port) admin_port="$2"; shift 2;;
    --bind) bind="$2"; shift 2;;
    --certs-dir) certs="$2"; shift 2;;
    --secrets-dir) secrets="$2"; shift 2;;
    --oidc-client-secret-file) oidc_secret_file="$2"; shift 2;;
    --proxy) proxy=1; shift;;
    --upstream-host) upstream_host="$2"; shift 2;;
    --gateway-cert) gw_cert="$2"; shift 2;;
    --gateway-key) gw_key="$2"; shift 2;;
    --admin-cert) adm_cert="$2"; shift 2;;
    --admin-key) adm_key="$2"; shift 2;;
    --ca-file) ca_file="$2"; shift 2;;
    --renew-certs) renew=1; shift;;
    --yes|-y) yes=1; shift;;
    -h|--help) usage 0;;
    *) echo "unknown option: $1" >&2; usage 1;;
  esac
done
command -v openssl >/dev/null || die "openssl is required"

ask() { # ask VAR "prompt" [default]
  local var="$1" prompt="$2" default="${3:-}" reply
  [ -n "${!var}" ] && return
  if [ "$yes" = 1 ] || [ ! -t 0 ]; then
    [ -n "$default" ] || die "$prompt is required (pass the matching option)"
    printf -v "$var" '%s' "$default"; return
  fi
  read -r -p "$prompt${default:+ [$default]}: " reply
  reply="${reply:-$default}"; [ -n "$reply" ] || die "$prompt is required"
  printf -v "$var" '%s' "$reply"
}

if [ "$renew" = 0 ] || [ ! -f "$env_file" ]; then
  echo "Ume LLM Gateway production setup"
  ask oidc_authority "OIDC authority URL (https://...)"
  case "$oidc_authority" in https://*) ;; *) die "OIDC authority must be an https:// URL";; esac
  ask gateway_host "Public hostname for the gateway API" "$(hostname -f 2>/dev/null || hostname)"
  if [ "$proxy" = 0 ] && [ "$yes" = 0 ] && [ -t 0 ]; then
    read -r -p "Is a reverse proxy (nginx, Nginx Proxy Manager, F5...) in front terminating TLS? [y/N]: " reply
    case "$reply" in [yY]*) proxy=1;; esac
  fi
  if [ "$proxy" = 1 ]; then
    ask admin_host "Public hostname for the admin UI (must differ from the gateway's)"
    [ "$admin_host" != "$gateway_host" ] || die "proxy mode needs two distinct hostnames (the proxy routes by name)"
    ask upstream_host "Address the proxy uses to reach this host" "$(hostname -f 2>/dev/null || hostname)"
  else
    ask admin_host "Public hostname for the admin UI" "$gateway_host"
  fi
fi
[ -z "$oidc_secret_file" ] || [ -r "$oidc_secret_file" ] || die "cannot read --oidc-client-secret-file $oidc_secret_file"
pki=0
if [ -n "$gw_cert$gw_key$adm_cert$adm_key" ]; then
  pki=1
  for f in "$gw_cert" "$gw_key" "$adm_cert" "$adm_key"; do
    [ -n "$f" ] && [ -r "$f" ] || die "PKI mode needs readable --gateway-cert, --gateway-key, --admin-cert and --admin-key"
  done
  [ -z "$ca_file" ] || [ -r "$ca_file" ] || die "cannot read --ca-file $ca_file"
fi
if [ -z "$bind" ]; then bind="127.0.0.1"; [ "$proxy" = 1 ] && bind="0.0.0.0"; fi
if [ "$proxy" = 1 ]; then public_gw=""; public_admin=""; else public_gw=":$gateway_port"; public_admin=":$admin_port"; fi

new_file() { # new_file PATH — true (and creates parent) if PATH does not exist yet
  [ -e "$1" ] && return 1
  mkdir -p "$(dirname "$1")"; return 0
}
rand() { openssl rand -hex 32; }

mkdir -p "$certs" "$secrets"

# --- Internal CA (trusted only by the services themselves) ----------------------
if new_file "$certs/ca.pem"; then
  openssl req -x509 -newkey rsa:3072 -nodes -keyout "$certs/ca.key" -out "$certs/ca.pem" \
    -days 3650 -subj "/CN=ume-gateway-internal-ca" >/dev/null 2>&1
fi

# --- Service certificates ---------------------------------------------------------
issue() { # issue SERVICE SAN...
  local svc="$1"; shift; local san="DNS:$svc,DNS:localhost" h tmp
  for h in "$@"; do [ -n "$h" ] && san="$san,DNS:$h"; done
  mkdir -p "$certs/$svc"; tmp="$(mktemp -d)"
  openssl req -newkey rsa:3072 -nodes -keyout "$certs/$svc/$svc.key" -out "$tmp/leaf.csr" -subj "/CN=$svc" >/dev/null 2>&1
  printf 'subjectAltName=%s\nextendedKeyUsage=serverAuth\n' "$san" > "$tmp/leaf.ext"
  openssl x509 -req -in "$tmp/leaf.csr" -CA "$certs/ca.pem" -CAkey "$certs/ca.key" -CAcreateserial \
    -CAserial "$tmp/ca.srl" -out "$certs/$svc/$svc.crt" -days 825 -extfile "$tmp/leaf.ext" >/dev/null 2>&1
  rm -rf "$tmp"; : > "$certs/$svc/.internal"
}
if [ "$renew" = 1 ] && [ -f "$env_file" ]; then
  # shellcheck disable=SC1090
  . "$env_file"
  gateway_host="${UME_GATEWAY_PUBLIC_URL#https://}"; gateway_host="${gateway_host%%[:/]*}"
  admin_host="${UME_ADMIN_PUBLIC_HOST:-$gateway_host}"
fi
for svc in postgres redis; do
  if [ "$renew" = 1 ] || new_file "$certs/$svc/$svc.crt"; then issue "$svc"; fi
done
use_pki() { # use_pki SERVICE CERT KEY — install operator-supplied PKI files
  mkdir -p "$certs/$1"; cp "$2" "$certs/$1/$1.crt"; cp "$3" "$certs/$1/$1.key"; rm -f "$certs/$1/.internal"
}
if [ "$pki" = 1 ]; then
  use_pki gateway "$gw_cert" "$gw_key"; use_pki adminapi "$adm_cert" "$adm_key"
else
  # On renewal only re-issue what this script issued (PKI-supplied certificates have no marker).
  if { [ "$renew" = 1 ] && [ -e "$certs/gateway/.internal" ]; } || new_file "$certs/gateway/gateway.crt"; then issue gateway "$gateway_host"; fi
  if { [ "$renew" = 1 ] && [ -e "$certs/adminapi/.internal" ]; } || new_file "$certs/adminapi/adminapi.crt"; then issue adminapi "$admin_host"; fi
fi

# --- Data Protection certificate (stable: never regenerate once data exists) --------
dp="$certs/data-protection.pfx"
if new_file "$dp"; then
  tmp="$(mktemp -d)"
  openssl req -x509 -newkey rsa:3072 -nodes -keyout "$tmp/dp.key" -out "$tmp/dp.crt" -days 3650 \
    -subj "/CN=ume-gateway-data-protection" >/dev/null 2>&1
  openssl pkcs12 -export -inkey "$tmp/dp.key" -in "$tmp/dp.crt" -out "$dp" -passout pass: >/dev/null 2>&1
  rm -rf "$tmp"
fi
for svc in gateway adminapi migrations; do
  mkdir -p "$certs/$svc"
  cp "$dp" "$certs/$svc/data-protection.pfx"
  cp "$certs/ca.pem" "$certs/$svc/ca.pem"
done
for svc in postgres redis; do cp "$certs/ca.pem" "$certs/$svc/ca.pem"; done
# Services trust the internal CA plus, when given, your PKI chain (their only trust store).
if [ -n "$ca_file" ]; then
  for svc in gateway adminapi; do cat "$ca_file" >> "$certs/$svc/ca.pem"; done
fi

# --- Secrets ----------------------------------------------------------------------
mkdir -p "$secrets"/{postgres,redis,gateway,adminapi,migrations}
for role in bootstrap migrator gateway admin; do
  new_file "$secrets/postgres/${role}_password" && rand > "$secrets/postgres/${role}_password"
done
new_file "$secrets/redis/redis_password" && rand > "$secrets/redis/redis_password"
if new_file "$secrets/redis/users.acl"; then
  hash="$(tr -d '\n' < "$secrets/redis/redis_password" | openssl dgst -sha256 | awk '{print $NF}')"
  sed "s/REPLACE_WITH_SHA256_PASSWORD_HASH/$hash/" "$here/redis/users.acl.example" > "$secrets/redis/users.acl"
fi
pepper_src="$secrets/.pepper"
new_file "$pepper_src" && openssl rand -hex 48 > "$pepper_src"
for svc in gateway adminapi migrations; do
  role="$svc"; case "$svc" in adminapi) role=admin;; migrations) role=migrator;; esac
  printf '%s' "$(cat "$pepper_src")" > "$secrets/$svc/Security__KeyPepper"
  printf 'Host=postgres;Database=gatewaydb;Username=ume_%s;Password=%s;SSL Mode=VerifyFull;Root Certificate=/run/certs/ca.pem;GSS Encryption Mode=Disable' \
    "$role" "$(cat "$secrets/postgres/${role}_password")" > "$secrets/$svc/ConnectionStrings__gatewaydb"
  if [ "$svc" != migrations ]; then
    printf 'redis:6379,user=gateway,password=%s,ssl=true,sslHost=redis,abortConnect=false' \
      "$(cat "$secrets/redis/redis_password")" > "$secrets/$svc/ConnectionStrings__redis"
  fi
done

if [ -n "$oidc_secret_file" ]; then
  tr -d '\r\n' < "$oidc_secret_file" > "$secrets/adminapi/Oidc__ClientSecret"
fi

# --- Ownership: container UIDs must be able to read their own files -------------------
own() { # own UID DIR... (needs root; falls back to a printed command)
  local id="$1"; shift
  if [ "$(id -u)" = 0 ]; then chown -R "$id:$id" "$@"; return; fi
  if command -v sudo >/dev/null && sudo chown -R "$id:$id" "$@"; then return; fi
  chown_todo+=("chown -R $id:$id $*")
}
chown_todo=()
own 70 "$certs/postgres" "$secrets/postgres"
own 999 "$certs/redis" "$secrets/redis"
own 1654 "$certs/gateway" "$certs/adminapi" "$certs/migrations" \
  "$secrets/gateway" "$secrets/adminapi" "$secrets/migrations"
chmod 755 "$certs" "$secrets" 2>/dev/null || true

# --- deploy/.env (non-secret settings only) ------------------------------------------
if [ "$renew" = 0 ] || [ ! -f "$env_file" ]; then
  if [ -e "$env_file" ]; then
    mv "$env_file" "$env_file.bak"; echo "Existing .env saved as $(basename "$env_file").bak"
  fi
  ops_url=""
  # PKI certificates rarely cover the internal name "gateway", so the admin API reaches
  # the gateway's operations endpoint by its public name instead.
  [ "$pki" = 1 ] && ops_url="UME_GATEWAY_OPERATIONS_URL=https://$gateway_host$public_gw"
  cat > "$env_file" <<EOF
# Non-secret settings for deploy/compose.prod.yaml. Secrets live in UME_SECRETS_DIR.
UME_CERTS_DIR=$certs
UME_SECRETS_DIR=$secrets
UME_OIDC_AUTHORITY=$oidc_authority
UME_OIDC_CLIENT_ID=$oidc_client
UME_GATEWAY_PUBLIC_URL=https://$gateway_host$public_gw
UME_ADMIN_PUBLIC_HOST=$admin_host
$ops_url
# Listeners default to loopback (0.0.0.0 with --proxy: firewall them to the proxy only).
UME_GATEWAY_BIND=$bind
UME_GATEWAY_PORT=$gateway_port
UME_ADMIN_BIND=$bind
UME_ADMIN_PORT=$admin_port
# Claims from an existing IdP (Keycloak, AD FS, Entra ID); see docs/runbook.md, "Identity provider".
# UME_OIDC_ROLE_CLAIM=roles
# UME_OIDC_DEPARTMENT_CLAIM=departmentCodes
# UME_OIDC_GROUP_CLAIM=groups
# UME_OIDC_EXTRA_SCOPE=groups
# Groups (names as in the token, separated by ';') that grant each role without IdP-side roles:
# UME_OIDC_GROUPS_GATEWAY_ADMIN=GG-Llm-Admins
# UME_OIDC_GROUPS_DEPARTMENT_ADMIN=
# UME_OIDC_GROUPS_VIEWER=
# Pin images by digest once pushed to your registry, for example:
# GATEWAY_IMAGE=registry.example.se/ume/gateway@sha256:...
# ADMINAPI_IMAGE=registry.example.se/ume/adminapi@sha256:...
# MIGRATIONS_IMAGE=registry.example.se/ume/migrations@sha256:...
EOF
fi

proxy_note=""
if [ "$proxy" = 1 ]; then
  sed -e "s/@GATEWAY_HOST@/$gateway_host/g" -e "s/@ADMIN_HOST@/$admin_host/g" \
      -e "s/@UPSTREAM@/$upstream_host/g" -e "s/@GATEWAY_PORT@/$gateway_port/g" \
      -e "s/@ADMIN_PORT@/$admin_port/g" "$here/proxy/nginx.conf.example" > "$here/proxy/nginx.conf"
  proxy_note="
Reverse proxy: $here/proxy/nginx.conf is ready for nginx; for Nginx Proxy Manager or F5 use
the same settings (see docs/runbook.md, 'Reverse proxy and PKI'). Both hosts forward to
HTTPS upstreams: $upstream_host:$gateway_port (gateway) and $upstream_host:$admin_port (admin UI),
preserving the Host header. Restrict those ports to the proxy with the host firewall."
fi
own_step="  0. File ownership is set."
if [ ${#chown_todo[@]} -gt 0 ]; then
  own_step="  0. Run as root first (containers run as other users and must read their files):"
  for c in "${chown_todo[@]}"; do own_step="$own_step
       $c"; done
fi

cat <<EOF

Done. Generated:
  $env_file
  $certs   (internal CA, service certificates, Data Protection certificate)
  $secrets (database/Redis passwords, key pepper, connection strings)

Next steps:
$own_step
  1. Register these redirect URIs for client '$oidc_client' in your IdP (code + PKCE):
       https://$admin_host$public_admin/signin-oidc
       https://$admin_host$public_admin/signout-callback-oidc
  2. docker compose -f deploy/compose.prod.yaml --env-file deploy/.env up -d --wait
  3. Back up $secrets/.pepper and $certs/data-protection.pfx separately from database
     dumps. Without them a restored database is unusable.
  4. Internal certificates last 825 days; re-issue with: $0 --renew-certs
     To use your PKI instead, re-run with --gateway-cert/--gateway-key/--admin-cert/--admin-key.
$proxy_note
EOF
