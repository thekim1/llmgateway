#!/bin/sh
# One-shot bootstrap for compose.portainer.yaml: creates the internal CA, service certificates,
# Data Protection certificate, passwords, key pepper and Redis ACL in the ume-certs / ume-secrets
# volumes. Idempotent: existing secrets are never overwritten (a new pepper or Data Protection
# certificate would break existing data); service certificates are re-issued when they expire
# within 30 days or the hostnames change. Inputs come from environment variables.
set -eu
umask 077
C=/certs
S=/secrets
: "${UME_GATEWAY_HOST:?Set UME_GATEWAY_HOST (public hostname of the gateway API)}"
ADMIN_HOST="${UME_ADMIN_HOST:-$UME_GATEWAY_HOST}"
DATA_HOST="${UME_DATA_HOST:-$UME_GATEWAY_HOST}"
for d in postgres redis gateway adminapi dataapi migrations; do mkdir -p "$C/$d" "$S/$d"; done
rand() { openssl rand -hex 32; }
quiet() { "$@" >/dev/null 2>&1; }

if [ ! -f "$C/ca.pem" ]; then
  quiet openssl req -x509 -newkey rsa:3072 -nodes -keyout "$C/ca.key" -out "$C/ca.pem" -days 3650 -subj /CN=ume-gateway-internal-ca
fi

# issue SERVICE SAN-LIST: (re)issue unless a valid certificate for exactly these names exists
issue() {
  svc="$1"; san="$2"; dir="$C/$svc"
  if [ -f "$dir/$svc.crt" ] && [ "$(cat "$dir/.san" 2>/dev/null)" = "$san" ] &&
     quiet openssl x509 -checkend 2592000 -noout -in "$dir/$svc.crt"; then
    return
  fi
  echo "bootstrap: issuing certificate for $svc ($san)"
  quiet openssl req -newkey rsa:3072 -nodes -keyout "$dir/$svc.key" -out "/tmp/$svc.csr" -subj "/CN=$svc"
  printf 'subjectAltName=%s\nextendedKeyUsage=serverAuth\n' "$san" > "/tmp/$svc.ext"
  quiet openssl x509 -req -in "/tmp/$svc.csr" -CA "$C/ca.pem" -CAkey "$C/ca.key" -CAcreateserial -CAserial /tmp/ca.srl \
    -out "$dir/$svc.crt" -days 825 -extfile "/tmp/$svc.ext"
  printf '%s' "$san" > "$dir/.san"
}
issue postgres "DNS:postgres,DNS:localhost"
issue redis "DNS:redis,DNS:localhost"
issue gateway "DNS:gateway,DNS:localhost,DNS:$UME_GATEWAY_HOST"
issue adminapi "DNS:adminapi,DNS:localhost,DNS:$ADMIN_HOST"
issue dataapi "DNS:dataapi,DNS:localhost,DNS:$DATA_HOST"

if [ ! -f "$C/data-protection.pfx" ]; then
  quiet openssl req -x509 -newkey rsa:3072 -nodes -keyout /tmp/dp.key -out /tmp/dp.crt -days 3650 -subj /CN=ume-gateway-data-protection
  quiet openssl pkcs12 -export -inkey /tmp/dp.key -in /tmp/dp.crt -out "$C/data-protection.pfx" -passout pass:
fi
for d in gateway adminapi migrations; do cp "$C/data-protection.pfx" "$C/$d/data-protection.pfx"; done
for d in postgres redis gateway adminapi dataapi migrations; do cp "$C/ca.pem" "$C/$d/ca.pem"; done

for role in bootstrap migrator gateway admin data; do
  [ -f "$S/postgres/${role}_password" ] || rand > "$S/postgres/${role}_password"
done
[ -f "$S/redis/redis_password" ] || rand > "$S/redis/redis_password"
if [ ! -f "$S/redis/users.acl" ]; then
  hash="$(tr -d '\n' < "$S/redis/redis_password" | openssl dgst -sha256 | awk '{print $NF}')"
  sed "s/REPLACE_WITH_SHA256_PASSWORD_HASH/$hash/" /config/users.acl.example > "$S/redis/users.acl"
fi
[ -f "$S/.pepper" ] || openssl rand -hex 48 > "$S/.pepper"
for svc in gateway adminapi migrations; do
  role="$svc"; case "$svc" in adminapi) role=admin;; migrations) role=migrator;; esac
  printf '%s' "$(cat "$S/.pepper")" > "$S/$svc/Security__KeyPepper"
  printf 'Host=postgres;Database=gatewaydb;Username=ume_%s;Password=%s;SSL Mode=VerifyFull;Root Certificate=/run/certs/ca.pem;GSS Encryption Mode=Disable' \
    "$role" "$(cat "$S/postgres/${role}_password")" > "$S/$svc/ConnectionStrings__gatewaydb"
  if [ "$svc" != migrations ]; then
    printf 'redis:6379,user=gateway,password=%s,ssl=true,sslHost=redis,abortConnect=false' \
      "$(cat "$S/redis/redis_password")" > "$S/$svc/ConnectionStrings__redis"
  fi
done
# Data API (optional, profile "data"): read-only role, no pepper, no Redis.
printf 'Host=postgres;Database=gatewaydb;Username=ume_data;Password=%s;SSL Mode=VerifyFull;Root Certificate=/run/certs/ca.pem;GSS Encryption Mode=Disable' \
  "$(cat "$S/postgres/data_password")" > "$S/dataapi/ConnectionStrings__gatewaydb"
# Confidential OIDC client (optional); the file follows the variable, so removing it clears the secret.
if [ -n "${UME_OIDC_CLIENT_SECRET:-}" ]; then
  printf '%s' "$UME_OIDC_CLIENT_SECRET" > "$S/adminapi/Oidc__ClientSecret"
else
  rm -f "$S/adminapi/Oidc__ClientSecret"
fi

# Static configuration files (services run read-only, so they get them from a volume).
F=/conf
mkdir -p "$F/postgres-init" "$F/postgres" "$F/redis" "$F/migrations"
cp /config/postgres-init.sql "$F/postgres-init/10-roles.sql"
cp /config/pg_hba.conf "$F/postgres/pg_hba.conf"
cp /config/redis.conf "$F/redis/redis.conf"
cp /config/app-roles.sql "$F/migrations/app-roles.sql"
chmod -R a+rX "$F"

chown -R 70:70 "$C/postgres" "$S/postgres"
chown -R 999:999 "$C/redis" "$S/redis"
chown -R 1654:1654 "$C/gateway" "$C/adminapi" "$C/dataapi" "$C/migrations" "$S/gateway" "$S/adminapi" "$S/dataapi" "$S/migrations"
chmod 755 "$C" "$S"
echo "bootstrap: certificates and secrets ready"
