-- App Watcher: statement capture for PostgreSQL (DBA runs this once, as a superuser).
-- Logs data-changing statements of ONE database user (the app's login) to the server log, which
-- App Watcher reads as a text log (Settings > Data > SQL statement log, point it at the log folder).
-- Reads no data, changes no data. Remove the setting when done.

-- 1. only the app's login, only INSERT/UPDATE/DELETE (and DDL)
ALTER ROLE app_login SET log_statement = 'mod';          -- replace app_login with the app's database user

-- 2. make sure the log is a plain file with timestamps (postgresql.conf, then reload):
--    logging_collector = on
--    log_directory = 'log'              -- under the data directory, or an absolute path
--    log_line_prefix = '%m [%p] %u@%d '  -- time, pid, user and database at the start of every line
--    SELECT pg_reload_conf();

-- 3. off (when done)
-- ALTER ROLE app_login RESET log_statement;

-- Lines look like: 2026-01-01 10:00:00.123 UTC [1234] app_login@appdb LOG:  statement: INSERT INTO ...
-- App Watcher reads the INSERT/UPDATE/DELETE part and ties it to the screen in front at that moment.
