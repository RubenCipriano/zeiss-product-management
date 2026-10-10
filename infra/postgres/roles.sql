-- Least-privilege database users. Run by the database administrator (locally: the `roles` service of database.yaml,
-- on every `infra.sh up database`; on a real server once by hand, or by configuration management), never by the
-- application. Idempotent: safe to run again, e.g. to rotate a password.
--
--   psql -v db=product_management -v migrator_password=... -v app_password=... -f roles.sql
--
--   pm_migrator  owns the schema: creates and changes tables and indexes. Used only by the CI/CD migration job.
--   pm_app       the API at runtime: reads and writes rows, draws ids from the sequence. No DDL, cannot drop or
--                alter anything, so a bug or an injected query in the API cannot change the schema.
-- The administrator (the server's superuser) is used for nothing else.
\set ON_ERROR_STOP on

SELECT format('CREATE ROLE pm_migrator LOGIN PASSWORD %L', :'migrator_password')
WHERE NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'pm_migrator') \gexec
SELECT format('CREATE ROLE pm_app LOGIN PASSWORD %L', :'app_password')
WHERE NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'pm_app') \gexec

-- Keep the passwords in sync with the secret store on every run (rotation = change the secret, run this again).
ALTER ROLE pm_migrator PASSWORD :'migrator_password';
ALTER ROLE pm_app PASSWORD :'app_password';

-- Guard rails for the application's sessions: a runaway query or a transaction left open by a bug is ended by the
-- server instead of holding locks and connections forever.
ALTER ROLE pm_app SET statement_timeout = '30s';
ALTER ROLE pm_app SET idle_in_transaction_session_timeout = '60s';

-- Database: nobody connects unless granted.
REVOKE ALL ON DATABASE :"db" FROM PUBLIC;
GRANT CONNECT ON DATABASE :"db" TO pm_migrator, pm_app;
GRANT CREATE ON DATABASE :"db" TO pm_migrator;   -- trusted extensions (pg_trgm)

-- Schema: owned by the migrator; the application may only use it.
ALTER SCHEMA public OWNER TO pm_migrator;
REVOKE CREATE ON SCHEMA public FROM PUBLIC;
GRANT USAGE ON SCHEMA public TO pm_app;

-- Objects created before these roles existed (e.g. by an earlier setup as the superuser) move to the migrator.
-- Tables first: their identity/serial sequences follow them; then any standalone sequence (the product ids).
DO $$
DECLARE
  object regclass;
BEGIN
  FOR object IN
    SELECT c.oid::regclass FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
    WHERE n.nspname = 'public' AND c.relkind IN ('r', 'p') AND pg_get_userbyid(c.relowner) <> 'pm_migrator'
  LOOP
    EXECUTE format('ALTER TABLE %s OWNER TO pm_migrator', object);
  END LOOP;
  FOR object IN
    SELECT c.oid::regclass FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
    WHERE n.nspname = 'public' AND c.relkind = 'S' AND pg_get_userbyid(c.relowner) <> 'pm_migrator'
  LOOP
    EXECUTE format('ALTER SEQUENCE %s OWNER TO pm_migrator', object);
  END LOOP;
END
$$;

-- What the application may do, on today's objects and on everything the migrator creates later.
GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public TO pm_app;
GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA public TO pm_app;
ALTER DEFAULT PRIVILEGES FOR ROLE pm_migrator IN SCHEMA public
  GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO pm_app;
ALTER DEFAULT PRIVILEGES FOR ROLE pm_migrator IN SCHEMA public
  GRANT USAGE, SELECT ON SEQUENCES TO pm_app;
