-- App Watcher: statement capture for Oracle (DBA runs this, as SYS or a user with ALTER SESSION/ALTER SYSTEM).
-- Produces a SQL trace file for the app's sessions. Oracle trace files are verbose; the simplest hand-over is
-- a text export of the executed statements with timestamps (one per line), which App Watcher reads as a text log.

-- Option A: trace the app's sessions by service or module (10046 trace), then tkprof or copy the raw files
BEGIN
  DBMS_MONITOR.CLIENT_ID_TRACE_ENABLE(client_id => 'APP_USER', waits => FALSE, binds => TRUE);   -- or SERV_MOD_ACT_TRACE_ENABLE for a module name
END;
/
-- Trace files land in the diagnostic_dest (SELECT value FROM v$diag_info WHERE name = 'Diag Trace';).
-- Turn off:  EXEC DBMS_MONITOR.CLIENT_ID_TRACE_DISABLE('APP_USER');

-- Option B (cheaper, periodic export): dump the statements the app's user ran, with their last time, to a text log
SET LINESIZE 4000 PAGESIZE 0 TRIMSPOOL ON
SPOOL C:\ProgramData\AppWatcher\sql\oracle-statements.log
SELECT TO_CHAR(last_active_time, 'YYYY-MM-DD HH24:MI:SS') || ' ' || sql_text
FROM   v$sql
WHERE  parsing_schema_name = 'APP_USER'
AND    REGEXP_LIKE(sql_text, '^\s*(INSERT|UPDATE|DELETE)', 'i')
ORDER  BY last_active_time;
SPOOL OFF
-- Run it every few minutes (scheduler job) so App Watcher can tie statements to the screens in front.
-- Bind values are not in v$sql; a 10046 trace with binds => TRUE is needed to map typed values to columns.
