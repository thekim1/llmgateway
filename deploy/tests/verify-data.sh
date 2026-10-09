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
    # Routing rules: the admin role writes them (and cascades to targets), the gateway role only reads. Rolled back.
    rule_id=00000000-0000-7000-8000-000000000001
    psql -Xq -v ON_ERROR_STOP=1 <<SQL >/dev/null
BEGIN;
INSERT INTO "RoutingRules" ("Id","Name","IsEnabled","Priority","Scope","Condition","Chain","Fallbacks","CreatedAt","UpdatedAt")
  VALUES ('$rule_id','proof',true,0,'Global','',false,'{}',now(),now());
INSERT INTO "RoutingRuleTargets" ("Id","RoutingRuleId","Model","Weight") VALUES ('00000000-0000-7000-8000-000000000002','$rule_id','proof',1);
UPDATE "RoutingRules" SET "IsEnabled"=false WHERE "Id"='$rule_id';
DELETE FROM "RoutingRules" WHERE "Id"='$rule_id';
SELECT 1 / (count(*) = 0)::int FROM "RoutingRuleTargets" WHERE "RoutingRuleId"='$rule_id';
ROLLBACK;
SQL
    export PGUSER=ume_gateway PGPASSWORD="$(cat /run/postgres-secrets/gateway_password)"
    test "$(psql -XAtqc 'SELECT count(*) >= 0 FROM "RoutingRules" r LEFT JOIN "RoutingRuleTargets" t ON t."RoutingRuleId" = r."Id"')" = t
    if psql -Xq -c "UPDATE \"RoutingRules\" SET \"IsEnabled\"=false" >/dev/null 2>&1; then exit 1; fi
    if psql -Xq -c "DELETE FROM \"RoutingRuleTargets\"" >/dev/null 2>&1; then exit 1; fi
    export PGUSER=ume_admin PGPASSWORD="$(cat /run/postgres-secrets/admin_password)"
    if psql -Xq -c 'DELETE FROM "AuditLog"' >/dev/null 2>&1; then exit 1; fi
    if psql -Xq -c 'DELETE FROM "UsageRecords"' >/dev/null 2>&1; then exit 1; fi
    export PGSSLMODE=disable
    if psql -Xq -c 'SELECT 1' >/dev/null 2>&1; then exit 1; fi
    printf 'Postgres TLS, restricted DML roles (including routing rules) and append-only audit verified.\n'
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
