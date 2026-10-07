#!/bin/sh
set -eu
case "$1" in
  postgres)
    export PGHOST=localhost PGDATABASE=gatewaydb PGSSLMODE=verify-full PGSSLROOTCERT=/run/certs/ca.pem
    export PGUSER=ume_gateway PGPASSWORD="$(cat /run/postgres-secrets/gateway_password)"
    test "$(psql -XAtqc 'SELECT count(*) >= 0 FROM "VirtualKeys"')" = t
    test "$(psql -XAtqc "SELECT NOT rolsuper AND NOT rolcreatedb AND NOT rolcreaterole FROM pg_roles WHERE rolname=current_user")" = t
    test "$(psql -XAtqc 'SELECT ssl FROM pg_stat_ssl WHERE pid=pg_backend_pid()')" = t
    if psql -Xq -c 'CREATE TABLE forbidden(id int)' >/dev/null 2>&1; then exit 1; fi
    if psql -Xq -c 'DELETE FROM "VirtualKeys"' >/dev/null 2>&1; then exit 1; fi
    if psql -Xq -c 'DELETE FROM "AuditLog"' >/dev/null 2>&1; then exit 1; fi
    export PGUSER=ume_admin PGPASSWORD="$(cat /run/postgres-secrets/admin_password)"
    if psql -Xq -c 'DELETE FROM "AuditLog"' >/dev/null 2>&1; then exit 1; fi
    if psql -Xq -c 'DELETE FROM "UsageRecords"' >/dev/null 2>&1; then exit 1; fi
    export PGSSLMODE=disable
    if psql -Xq -c 'SELECT 1' >/dev/null 2>&1; then exit 1; fi
    printf 'Postgres TLS, restricted DML roles and append-only audit verified.\n'
    ;;
  redis)
    redis-cli --tls --cacert /run/certs/ca.pem -h localhost PING | grep -q NOAUTH
    export REDISCLI_AUTH="$(cat /run/redis-secrets/test_password)"
    cli() { redis-cli --tls --cacert /run/certs/ca.pem -h localhost --user gateway "$@"; }
    test "$(cli PING)" = PONG
    test "$(cli SET ume:proof 1)" = OK
    test "$(cli EVAL "return redis.call('INCR', KEYS[1])" 1 ume:proof)" = 2
    cli SET outside 1 | grep -q NOPERM
    cli FLUSHALL | grep -q NOPERM
    cli CONFIG GET '*' | grep -q NOPERM
    cli KEYS '*' | grep -q NOPERM
    cli DEL ume:proof >/dev/null
    printf 'Redis TLS, authentication, namespace ACL and forbidden commands verified.\n'
    ;;
  *) exit 1;;
esac
