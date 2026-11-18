-- App Watcher: statement capture for MySQL / MariaDB (DBA runs this once, as an administrator).
-- Records every statement to a text file App Watcher can read (Settings > Data > SQL statement log,
-- point it at the folder holding the file). It reads no data and changes no data.
-- The general log slows the server a little and grows fast: turn it off when done.

-- 1. where the file goes (the server account must be able to write there)
SET GLOBAL general_log_file = 'C:/ProgramData/AppWatcher/sql/mysql-general.log';   -- Linux: /var/log/mysql/appwatcher-general.log
SET GLOBAL log_output = 'FILE';

-- 2. on
SET GLOBAL general_log = 'ON';

-- Optional: only the app's own login, by creating a dedicated account for it and filtering the file by
-- that user id later; MySQL cannot filter the general log by application name itself.

-- 3. off (when done)
-- SET GLOBAL general_log = 'OFF';

-- The file format is: <time> <thread id> Query <statement>. App Watcher reads INSERT/UPDATE/DELETE lines
-- and ties them to the screen that was in front at that moment.
