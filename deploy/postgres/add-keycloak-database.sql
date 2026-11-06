-- Adds Keycloak's database and role (deploy/keycloak, docs/keycloak.md) to an existing installation; new installations
-- get them from init.sql when secrets/postgres/keycloak_password exists. Run as the database superuser after
-- deploy/keycloak/init-keycloak.sh has created that file. Safe to run more than once.
SET log_statement = 'none';
DO $$
BEGIN
  IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'ume_keycloak') THEN
    EXECUTE format('CREATE ROLE ume_keycloak LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE PASSWORD %L',
      btrim(pg_read_file('/run/postgres-secrets/keycloak_password'), E'\r\n '));
  END IF;
END $$;
SELECT 'CREATE DATABASE keycloak OWNER ume_keycloak' WHERE NOT EXISTS (SELECT FROM pg_database WHERE datname = 'keycloak')\gexec
REVOKE ALL ON DATABASE keycloak FROM PUBLIC;
GRANT CONNECT ON DATABASE keycloak TO ume_keycloak;
\connect keycloak
REVOKE ALL ON SCHEMA public FROM PUBLIC;
ALTER SCHEMA public OWNER TO ume_keycloak;
-- pg_hba.conf lists ume_keycloak; reload so the line applies without a restart.
SELECT pg_reload_conf();
