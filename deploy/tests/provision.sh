#!/bin/sh
set -eu
umask 077
cd /workspace
mkdir -p certs/postgres certs/redis certs/gateway certs/adminapi certs/migrations
mkdir -p secrets/postgres secrets/redis secrets/gateway secrets/adminapi secrets/migrations
openssl req -x509 -newkey rsa:2048 -nodes -keyout ca.key -out ca.pem -days 1 -subj /CN=ume-compose-test-ca >/dev/null 2>&1
for service in postgres redis gateway adminapi; do
  openssl req -newkey rsa:2048 -nodes -keyout "certs/$service/$service.key" -out leaf.csr -subj "/CN=$service" >/dev/null 2>&1
  printf 'subjectAltName=DNS:%s,DNS:localhost\nextendedKeyUsage=serverAuth\n' "$service" > leaf.ext
  openssl x509 -req -in leaf.csr -CA ca.pem -CAkey ca.key -CAcreateserial -out "certs/$service/$service.crt" -days 1 -extfile leaf.ext >/dev/null 2>&1
  cp ca.pem "certs/$service/ca.pem"
done
openssl pkcs12 -export -inkey certs/gateway/gateway.key -in certs/gateway/gateway.crt -out dp.pfx -passout pass: >/dev/null 2>&1
for service in gateway adminapi migrations; do
  cp dp.pfx "certs/$service/data-protection.pfx"
  cp ca.pem "certs/$service/ca.pem"
done
for role in bootstrap migrator gateway admin; do
  openssl rand -hex 32 > "secrets/postgres/${role}_password"
done
openssl rand -hex 32 > secrets/redis/test_password
redis_hash="$(tr -d '\n' < secrets/redis/test_password | openssl dgst -sha256 | awk '{print $2}')"
sed "s/REPLACE_WITH_SHA256_PASSWORD_HASH/$redis_hash/" /config/users.acl.example > secrets/redis/users.acl
pepper="$(openssl rand -hex 48)"
for service in gateway adminapi migrations; do
  printf '%s' "$pepper" > "secrets/$service/Security__KeyPepper"
  role="$service"
  case "$service" in adminapi) role=admin;; migrations) role=migrator;; esac
  password="$(cat "secrets/postgres/${role}_password")"
  printf 'Host=postgres;Database=gatewaydb;Username=ume_%s;Password=%s;SSL Mode=VerifyFull;Root Certificate=/run/certs/ca.pem;GSS Encryption Mode=Disable' "$role" "$password" > "secrets/$service/ConnectionStrings__gatewaydb"
  if [ "$service" != migrations ]; then
    printf 'redis:6379,user=gateway,password=%s,ssl=true,sslHost=redis,abortConnect=false' "$(cat secrets/redis/test_password)" > "secrets/$service/ConnectionStrings__redis"
  fi
done
chown -R 70:70 certs/postgres secrets/postgres
chown -R 999:999 certs/redis secrets/redis
chown -R 1654:1654 certs/gateway certs/adminapi certs/migrations secrets/gateway secrets/adminapi secrets/migrations
chmod 755 /workspace /workspace/certs /workspace/secrets
printf 'Ephemeral certificate and secret provisioning completed.\n'
