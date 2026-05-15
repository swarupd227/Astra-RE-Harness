-- Astra RE Harness — Postgres bootstrap
-- Runs once on first container start (data volume init only).
-- Subsequent schema changes go through EF Core migrations from the API.

CREATE EXTENSION IF NOT EXISTS "uuid-ossp";
CREATE EXTENSION IF NOT EXISTS "pg_trgm";

-- The Hangfire schema is created by the worker on startup.
-- The application schema is created by EF Core migrations from the API.

-- A small marker so it is obvious this script ran.
DO $$
BEGIN
    RAISE NOTICE 'Astra Postgres init complete: extensions uuid-ossp, pg_trgm enabled.';
END
$$;
