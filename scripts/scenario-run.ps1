<#
.SYNOPSIS
  Runs a scenario against the sample app under the watcher and scores what the watcher recorded (milestone M3).
  Starts App Watcher in portable mode on tests\SampleApp, starts the sample app, runs AppWatcher.Driver on it
  (real mouse and keyboard, only while the sample app is in front), stops both, learns, and writes the scorecard
  with AppWatcher.exe --verify. Kills only the processes it started.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File scripts\scenario-run.ps1
  powershell -ExecutionPolicy Bypass -File scripts\scenario-run.ps1 -Scenario scenarios\sample-erp.json
#>
param(
    [string]$Scenario = "",
    [string]$Watcher = "",
    [string]$Sample = "",
    [string]$Driver = ""
)
$ErrorActionPreference = "Stop"
$root = Resolve-Path (Join-Path $PSScriptRoot "..")
if ($Scenario -eq "") { $Scenario = Join-Path $root "scenarios\sample-erp.json" }
if ($Watcher -eq "") {
    $w = Get-ChildItem (Join-Path $root "src\AppWatcher\bin\Debug") -Recurse -Filter AppWatcher.exe -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if ($null -ne $w) { $Watcher = $w.FullName }
}
if ($Sample -eq "") { $Sample = Join-Path $root "tests\SampleApp\bin\Debug\net8.0-windows\SampleApp.exe" }
if ($Driver -eq "") { $Driver = Join-Path $root "src\AppWatcher.Driver\bin\Debug\net8.0-windows\AppWatcher.Driver.exe" }
if (-not (Test-Path $Scenario)) { throw "scenario not found: $Scenario" }
if (-not (Test-Path $Watcher)) { throw "watcher not built: $Watcher (dotnet build)" }
if (-not (Test-Path $Sample)) { throw "sample app not built: $Sample (dotnet build tests\SampleApp)" }
if (-not (Test-Path $Driver)) { throw "driver not built: $Driver (dotnet build src\AppWatcher.Driver)" }
if (-not $env:DOTNET_ROOT -and (Test-Path (Join-Path $env:USERPROFILE ".dotnet\dotnet.exe"))) { $env:DOTNET_ROOT = Join-Path $env:USERPROFILE ".dotnet" }
$dataRoot = Join-Path (Split-Path $Watcher) "AppWatcherData"
if (Test-Path $dataRoot) { Remove-Item $dataRoot -Recurse -Force }
Remove-Item (Join-Path (Split-Path $Sample) "sample-data.json") -ErrorAction SilentlyContinue

function Run-Watcher([string[]]$cmdArgs) {
    $p = Start-Process -FilePath $Watcher -ArgumentList (@("--portable") + $cmdArgs) -Wait -PassThru -NoNewWindow
    return $p.ExitCode
}

Write-Host "1. headless setup of the watcher on the sample app"
$code = Run-Watcher @("--setup", "`"$Sample`"", "test1234")
if ($code -ne 0) { throw "setup failed ($code)" }
$cfgPath = Join-Path $dataRoot "config.json"
$cfg = Get-Content $cfgPath -Raw | ConvertFrom-Json
$cfg.dataSources = @(@{ kind = "file"; path = (Join-Path (Split-Path $Sample) "sample-data.json"); label = "sample-data.json"; enabled = $true })
$cfg.snapshot.afterDelayMs = 800
$cfg.autoDetectPending = $false
$cfg | ConvertTo-Json -Depth 10 | Set-Content $cfgPath -Encoding utf8

Write-Host "2. start watcher (tray) and sample app"
$watch = Start-Process -FilePath $Watcher -ArgumentList "--portable" -PassThru
Start-Sleep -Milliseconds 1500
$app = Start-Process -FilePath $Sample -ArgumentList "--corner" -PassThru
Start-Sleep -Milliseconds 2500
$finished = $false
try {
    $stamp = Get-Date -Format "yyyy-MM-dd-HHmmss"
    $runLog = Join-Path $env:TEMP ("scenario-run-" + $stamp + ".jsonl")
    Write-Host ("3. drive the scenario (process {0}): {1}" -f $app.Id, $Scenario)
    $d = Start-Process -FilePath $Driver -ArgumentList @("`"$Scenario`"", "--pid", $app.Id, "--test-environment", "--yes", "--out", "`"$runLog`"") -Wait -PassThru -NoNewWindow
    Write-Host ("   driver exit code {0}; run log {1}" -f $d.ExitCode, $runLog)
    Start-Sleep -Milliseconds 2500
    Write-Host "4. stop the sample app (killed: it is ours), learn, stop the watcher"
    if (-not $app.HasExited) { Stop-Process -Id $app.Id -Force }
    Start-Sleep -Milliseconds 5000   # the watcher checks the process every 3 s and saves its status when it sees it exit
    $code = Run-Watcher @("--learn")
    if ($code -ne 0) { throw "learn failed ($code)" }
    Stop-Process -Id $watch.Id -Force
    Write-Host "5. score"
    $code = Run-Watcher @("--verify", "`"$Scenario`"", "`"$runLog`"")
    if ($code -ne 0) { throw "verify failed ($code)" }
    $data = (Get-ChildItem (Join-Path $dataRoot "data") -Directory | Select-Object -First 1).FullName
    $card = Get-ChildItem (Join-Path $data "verification") -Filter *.md | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    Write-Host ("   scorecard: {0}" -f $card.FullName)
    Get-Content $card.FullName -Encoding UTF8 | Select-Object -First 16 | ForEach-Object { Write-Host ("     " + $_) }
    Get-Content $card.FullName -Encoding UTF8 | Where-Object { $_ -match '\| (failed|blocked) \|' -or $_ -like '- *' -or $_ -like 'Presses recorded*' -or $_ -like 'No press*' } | ForEach-Object { Write-Host ("     " + $_) }
    $finished = $true
} finally {
    if (-not $finished) { foreach ($p in @($app, $watch)) { try { Stop-Process -Id $p.Id -Force -ErrorAction Stop } catch { } } }
}
