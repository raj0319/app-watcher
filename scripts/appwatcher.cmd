@echo off
rem App Watcher command-line wrapper. AppWatcher.exe is a windowed program, so a plain shell does not wait
rem for it; this wrapper does (start /wait) and passes the exit code and the output back.
rem   appwatcher --learn | --rebuild | --export | --status | --expect | --detect <exe> | --merge <folder or zip>
setlocal
set "EXE=%~dp0AppWatcher.exe"
if not exist "%EXE%" set "EXE=%LOCALAPPDATA%\AppWatcher\AppWatcher.exe"
if not exist "%EXE%" set "EXE=%~dp0..\publish\AppWatcher.exe"
if not exist "%EXE%" (
  echo AppWatcher.exe not found next to this script, in %%LOCALAPPDATA%%\AppWatcher or in publish\. 1>&2
  exit /b 9
)
start "" /b /wait "%EXE%" %*
exit /b %ERRORLEVEL%
