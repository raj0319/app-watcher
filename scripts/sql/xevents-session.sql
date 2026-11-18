/*
  App Watcher: capture the SQL statements one application sends to SQL Server (Express included).

  What it does
    Creates an Extended Events session that records every completed statement and RPC call whose
    client application name equals the watched app's name, with the statement text, to .xel files.
    It is read-only towards the application's data: it only observes what the server executes.

  Who runs it
    A DBA or whoever has ALTER ANY EVENT SESSION on the server (sysadmin on Express). Run once.

  Before you run it
    1. Replace  MyApp  below with the application name the app uses when it connects. Find it with:
         SELECT DISTINCT program_name FROM sys.dm_exec_sessions WHERE program_name <> '';
       while the app is running.
    2. Replace  C:\AppWatcherSql\  with a folder the SQL Server service account can write to.
       Point App Watcher at that folder in Settings > Data > "SQL statement log".
  Afterwards
    The .xel files roll at 100 MB, 5 files kept (500 MB at most). App Watcher reads them at learn time.
    If you would rather hand over a CSV: run the export query at the bottom and save the result as CSV
    with two columns (timestamp, statement).
*/

IF EXISTS (SELECT 1 FROM sys.server_event_sessions WHERE name = 'AppWatcher_Statements')
    DROP EVENT SESSION AppWatcher_Statements ON SERVER;
GO

CREATE EVENT SESSION AppWatcher_Statements ON SERVER
ADD EVENT sqlserver.sql_statement_completed
(
    ACTION (sqlserver.client_app_name, sqlserver.database_name, sqlserver.session_id, sqlserver.sql_text)
    WHERE sqlserver.client_app_name = N'MyApp'
),
ADD EVENT sqlserver.rpc_completed
(
    ACTION (sqlserver.client_app_name, sqlserver.database_name, sqlserver.session_id, sqlserver.sql_text)
    WHERE sqlserver.client_app_name = N'MyApp'
)
ADD TARGET package0.event_file
(
    SET filename = N'C:\AppWatcherSql\AppWatcher_Statements.xel',
        max_file_size = 100,          -- MB per file
        max_rollover_files = 5
)
WITH (MAX_MEMORY = 4096 KB, EVENT_RETENTION_MODE = ALLOW_SINGLE_EVENT_LOSS,
      MAX_DISPATCH_LATENCY = 5 SECONDS, STARTUP_STATE = ON);
GO

ALTER EVENT SESSION AppWatcher_Statements ON SERVER STATE = START;
GO

/* ---- To stop and remove the capture (run when the observation period is over) ----

ALTER EVENT SESSION AppWatcher_Statements ON SERVER STATE = STOP;
DROP EVENT SESSION AppWatcher_Statements ON SERVER;

*/

/* ---- Optional: export to CSV instead of handing over the .xel files ----
   Run while the session exists, save the result grid as CSV (timestamp, statement).

SELECT
    CONVERT(varchar(30), DATEADD(mi, DATEDIFF(mi, GETUTCDATE(), GETDATE()), x.value('(event/@timestamp)[1]', 'datetime2')), 126) AS [timestamp],
    COALESCE(x.value('(event/data[@name="statement"]/value)[1]', 'nvarchar(max)'),
             x.value('(event/action[@name="sql_text"]/value)[1]', 'nvarchar(max)')) AS [statement]
FROM sys.fn_xe_file_target_read_file(N'C:\AppWatcherSql\AppWatcher_Statements*.xel', NULL, NULL, NULL) AS f
CROSS APPLY (SELECT CAST(f.event_data AS xml)) AS t(x)
ORDER BY 1;

*/
