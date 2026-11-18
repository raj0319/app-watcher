/*
  App Watcher: optional one-time setup so snapshots of a SQL Server database are cheap.

  With Change Tracking on, App Watcher asks the server "which tables changed since version N?" and
  only re-reads those tables, instead of reading every table before and after each save.
  Change Tracking is a built-in SQL Server feature (Express included). It keeps a small amount of
  bookkeeping per tracked table; it does not change the application's data.

  Run as a DBA. Replace MyAppDb with the database name. Add one ALTER TABLE line per table you want
  tracked (every table the app writes is best). Tables need a primary key.
*/

ALTER DATABASE MyAppDb
    SET CHANGE_TRACKING = ON (CHANGE_RETENTION = 7 DAYS, AUTO_CLEANUP = ON);
GO

USE MyAppDb;
GO

-- one line per table, for example:
-- ALTER TABLE dbo.Customers ENABLE CHANGE_TRACKING;
-- ALTER TABLE dbo.Orders    ENABLE CHANGE_TRACKING;

-- Or enable it for every table that has a primary key:
DECLARE @sql nvarchar(max) = N'';
SELECT @sql += N'ALTER TABLE ' + QUOTENAME(s.name) + N'.' + QUOTENAME(t.name) + N' ENABLE CHANGE_TRACKING;' + CHAR(10)
FROM sys.tables t
JOIN sys.schemas s ON s.schema_id = t.schema_id
WHERE t.is_ms_shipped = 0
  AND EXISTS (SELECT 1 FROM sys.key_constraints k WHERE k.parent_object_id = t.object_id AND k.type = 'PK')
  AND NOT EXISTS (SELECT 1 FROM sys.change_tracking_tables c WHERE c.object_id = t.object_id);
EXEC sp_executesql @sql;
GO

/* ---- To turn it off again ----
-- ALTER TABLE dbo.Customers DISABLE CHANGE_TRACKING;   (per table)
-- ALTER DATABASE MyAppDb SET CHANGE_TRACKING = OFF;
*/
