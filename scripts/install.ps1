<#
.SYNOPSIS
  Installs App Watcher for the current Windows user and makes it start at login.

.DESCRIPTION
  Copies AppWatcher.exe to %LOCALAPPDATA%\AppWatcher, then registers it to start at login:
    - default: a "Run" registry key for this user (no admin rights needed)
    - with -ScheduledTask: a Scheduled Task that starts at logon (survives Run-key cleanups; may ask for admin)
  Then starts it. The first start opens the setup window (choose the app, set the password).

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File scripts\install.ps1
  powershell -ExecutionPolicy Bypass -File scripts\install.ps1 -ScheduledTask
  powershell -ExecutionPolicy Bypass -File scripts\install.ps1 -ExePath C:\Downloads\AppWatcher.exe
#>
param(
    [string]$ExePath = "",
    [switch]$ScheduledTask,
    [switch]$NoStart
)

$ErrorActionPreference = "Stop"

if ($ExePath -eq "") {
    $candidates = @(
        (Join-Path $PSScriptRoot "..\publish\AppWatcher.exe"),
        (Join-Path $PSScriptRoot "AppWatcher.exe"),
        (Join-Path (Get-Location) "AppWatcher.exe")
    )
    foreach ($c in $candidates) { if (Test-Path $c) { $ExePath = (Resolve-Path $c).Path; break } }
}
if ($ExePath -eq "" -or -not (Test-Path $ExePath)) {
    Write-Host "AppWatcher.exe not found. Build it first (scripts\publish.ps1) or pass -ExePath." -ForegroundColor Red
    exit 1
}

$target = Join-Path $env:LOCALAPPDATA "AppWatcher"
New-Item -ItemType Directory -Force $target | Out-Null
$dest = Join-Path $target "AppWatcher.exe"

# stop a running copy so the file can be replaced
Get-Process -Name AppWatcher -ErrorAction SilentlyContinue | ForEach-Object {
    Write-Host "Stopping the running App Watcher (it will be restarted)..."
    $_.CloseMainWindow() | Out-Null
    if (-not $_.WaitForExit(3000)) { $_.Kill() }
}
Copy-Item $ExePath $dest -Force
Write-Host "Installed to $dest"
# command-line wrapper (start /wait) so scripts can call AppWatcher.exe --learn and get its output
$cmd = Join-Path $PSScriptRoot "appwatcher.cmd"
if (Test-Path $cmd) { Copy-Item $cmd (Join-Path $target "appwatcher.cmd") -Force }

if ($ScheduledTask) {
    $action = New-ScheduledTaskAction -Execute $dest
    $trigger = New-ScheduledTaskTrigger -AtLogOn -User $env:USERNAME
    $settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit ([TimeSpan]::Zero) -MultipleInstances IgnoreNew
    Register-ScheduledTask -TaskName "AppWatcher" -Action $action -Trigger $trigger -Settings $settings -Description "App Watcher: learns the logic behind one chosen desktop app" -Force | Out-Null
    Remove-ItemProperty -Path "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run" -Name "AppWatcher" -ErrorAction SilentlyContinue
    Write-Host "Starts at login through the Scheduled Task 'AppWatcher'."
} else {
    New-Item -Path "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run" -Force | Out-Null
    Set-ItemProperty -Path "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run" -Name "AppWatcher" -Value "`"$dest`""
    Write-Host "Starts at login through the Run key for this user."
}

if (-not $NoStart) {
    Start-Process $dest
    Write-Host "App Watcher started. Look for the eye icon in the tray; the setup window opens on the first run."
}
Write-Host "Data folder: $env:ProgramData\AppWatcher"
