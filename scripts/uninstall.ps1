<#
.SYNOPSIS
  Removes App Watcher: stops it, removes the login start (Run key and Scheduled Task), deletes the program file.

.DESCRIPTION
  The learned data in %ProgramData%\AppWatcher is KEPT unless you pass -RemoveData.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File scripts\uninstall.ps1
  powershell -ExecutionPolicy Bypass -File scripts\uninstall.ps1 -RemoveData
#>
param(
    [switch]$RemoveData
)

$ErrorActionPreference = "Continue"

Get-Process -Name AppWatcher -ErrorAction SilentlyContinue | ForEach-Object {
    Write-Host "Stopping App Watcher..."
    $_.Kill()
    $_.WaitForExit(5000) | Out-Null
}

Remove-ItemProperty -Path "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run" -Name "AppWatcher" -ErrorAction SilentlyContinue
Unregister-ScheduledTask -TaskName "AppWatcher" -Confirm:$false -ErrorAction SilentlyContinue

$target = Join-Path $env:LOCALAPPDATA "AppWatcher"
if (Test-Path $target) { Remove-Item $target -Recurse -Force; Write-Host "Removed $target" }

$data = Join-Path $env:ProgramData "AppWatcher"
if ($RemoveData) {
    if (Test-Path $data) { Remove-Item $data -Recurse -Force; Write-Host "Removed the data folder $data" }
} else {
    Write-Host "Kept the data folder $data (pass -RemoveData to delete it)."
}
Write-Host "App Watcher is uninstalled."
