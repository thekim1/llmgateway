SET log_statement = 'none';
DO $$
BEGIN
  EXECUTE format('CREATE ROLE ume_migrator LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE PASSWORD %L',
    btrim(pg_read_file('/run/postgres-secrets/migrator_password'), E'\r\n '));
  EXECUTE format('CREATE ROLE ume_gateway LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE PASSWORD %L',
    btrim(pg_read_file('/run/postgres-secrets/gateway_password'), E'\r\n '));
  EXECUTE format('CREATE ROLE ume_admin LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE PASSWORD %L',
    btrim(pg_read_file('/run/postgres-secrets/admin_password'), E'\r\n '));
END $$;
CREATE DATABASE gatewaydb OWNER ume_migrator;
REVOKE ALL ON DATABASE gatewaydb FROM PUBLIC;
GRANT CONNECT ON DATABASE gatewaydb TO ume_gateway, ume_admin;
\connect gatewaydb
REVOKE ALL ON SCHEMA public FROM PUBLIC;
ALTER SCHEMA public OWNER TO ume_migrator;
GRANT USAGE ON SCHEMA public TO ume_gateway, ume_admin;
