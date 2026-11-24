<#
.SYNOPSIS
  End-to-end test of the collector against a WEB PAGE in a Chromium window (tests\SampleWeb: a WinForms host with one
  WebView2 control, the same kind of tree Electron and browser-based ERPs give). Starts App Watcher in portable mode on
  the sample, drives it by UI Automation patterns (Invoke, Value, SelectionItem) on that one process's window only,
  then learns and prints what was recorded with a PASS/FAIL summary. Kills only the processes it started.

.EXAMPLE
  dotnet build tests\SampleWeb
  powershell -ExecutionPolicy Bypass -File scripts\live-test-web.ps1
  powershell -ExecutionPolicy Bypass -File scripts\live-test-web.ps1 -Watcher .\publish\AppWatcher.exe
#>
param(
    [string]$Watcher = "",
    [string]$Sample = "",
    [string]$Profile = "electron",
    [int]$SettleMs = 900
)
$ErrorActionPreference = "Stop"
$root = Resolve-Path (Join-Path $PSScriptRoot "..")
if ($Watcher -eq "") {
    $w = Get-ChildItem (Join-Path $root "src\AppWatcher\bin\Debug") -Recurse -Filter AppWatcher.exe -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if ($null -ne $w) { $Watcher = $w.FullName }
}
if ($Sample -eq "") { $Sample = Join-Path $root "tests\SampleWeb\bin\Debug\net8.0-windows\SampleWeb.exe" }
if (-not (Test-Path $Watcher)) { throw "watcher not built: $Watcher" }
if (-not (Test-Path $Sample)) { throw "sample web app not built: $Sample (dotnet build tests\SampleWeb)" }
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName Microsoft.VisualBasic
Add-Type -Namespace LiveTest -Name Msg -MemberDefinition @'
[DllImport("user32.dll")] public static extern bool SetForegroundWindow(System.IntPtr h);
public delegate bool EnumProc(System.IntPtr h, System.IntPtr l);
[DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc p, System.IntPtr l);
[DllImport("user32.dll")] public static extern bool IsWindowVisible(System.IntPtr h);
[DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(System.IntPtr h, out uint pid);
public static System.Collections.Generic.List<System.IntPtr> Visible(uint pid) { var r = new System.Collections.Generic.List<System.IntPtr>(); EnumWindows((h, l) => { uint p; GetWindowThreadProcessId(h, out p); if (p == pid && IsWindowVisible(h)) r.Add(h); return true; }, System.IntPtr.Zero); return r; }
'@

if (-not $env:DOTNET_ROOT -and (Test-Path (Join-Path $env:USERPROFILE ".dotnet\dotnet.exe"))) { $env:DOTNET_ROOT = Join-Path $env:USERPROFILE ".dotnet" }
$dataRoot = Join-Path (Split-Path $Watcher) "AppWatcherData"
if (Test-Path $dataRoot) { Remove-Item $dataRoot -Recurse -Force }

function Run-Watcher([string[]]$cmdArgs) {
    $p = Start-Process -FilePath $Watcher -ArgumentList (@("--portable") + $cmdArgs) -Wait -PassThru -NoNewWindow
    return $p.ExitCode
}

Write-Host "1. headless setup (profile ${Profile}: the host is a WinForms exe, the page inside is Chromium)"
$code = Run-Watcher @("--setup", "`"$Sample`"", "test1234", $Profile)
if ($code -ne 0) { throw "setup failed ($code)" }
$cfgPath = Join-Path $dataRoot "config.json"
$cfg = Get-Content $cfgPath -Raw | ConvertFrom-Json
Write-Host ("   toolkit: {0}, profile: {1}" -f $cfg.toolkit, $cfg.profile)
$cfg.autoDetectPending = $false   # the file-access detection asks for administrator permission; an unattended test must not raise it
$cfg | ConvertTo-Json -Depth 10 | Set-Content $cfgPath -Encoding utf8

Write-Host "2. start watcher (tray) and sample web app"
$watch = Start-Process -FilePath $Watcher -ArgumentList "--portable" -PassThru
Start-Sleep -Milliseconds 1500
$app = Start-Process -FilePath $Sample -ArgumentList "--corner" -PassThru
Start-Sleep -Milliseconds 4000   # WebView2 starts its own browser processes and loads the page

$UIA = [System.Windows.Automation.AutomationElement]
$TrueC = [System.Windows.Automation.Condition]::TrueCondition
function Top([string]$title, [int]$tries = 60) {
    # the host window of the process we started (the page lives inside it)
    foreach ($i in 1..$tries) {
        foreach ($hw in [LiveTest.Msg]::Visible([uint32]$app.Id)) { try { $e = $UIA::FromHandle($hw); if ($e.Current.Name -like $title) { return $e } } catch { } }
        Start-Sleep -Milliseconds 250
    }
    throw "window not found: $title"
}
function Walk($parent) { return $parent.FindAll([System.Windows.Automation.TreeScope]::Descendants, $TrueC) }
# a control is found by the page's id (Chromium exposes it as the AutomationId) or, failing that, by its accessible name and control type
function Find($parent, [string]$key, [string]$controlType = "") {
    foreach ($i in 1..20) {
        $byName = $null
        foreach ($d in (Walk $parent)) {
            if ($d.Current.AutomationId -eq $key) { return $d }
            if ($null -eq $byName -and $d.Current.Name -eq $key -and ($controlType -eq "" -or $d.Current.ControlType.ProgrammaticName -eq "ControlType.$controlType")) { $byName = $d }
        }
        if ($null -ne $byName) { return $byName }
        Start-Sleep -Milliseconds 400
    }
    throw "control not found: $key $controlType"
}
function Front() { try { [Microsoft.VisualBasic.Interaction]::AppActivate($app.Id) } catch { }; Start-Sleep -Milliseconds 400 }
function Act($parent, [string]$key, [string]$ct = "Button") {
    $e = Find $parent $key $ct
    try { $e.SetFocus() } catch { }
    Start-Sleep -Milliseconds 150
    try { $e.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() } catch { if ($_.Exception.Message -notlike "*timed out*") { throw } }
    Start-Sleep -Milliseconds $SettleMs
}
function SetVal($parent, [string]$key, [string]$value, [string]$leaveTo = "") {
    $e = Find $parent $key "Edit"
    $e.SetFocus(); Start-Sleep -Milliseconds 150
    $e.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($value)
    Start-Sleep -Milliseconds 300
    # the page reacts when the field loses focus (a change event): move the focus on, like a Tab key would
    if ($leaveTo -ne "") { (Find $parent $leaveTo "Edit").SetFocus(); Start-Sleep -Milliseconds 300 }
    Start-Sleep -Milliseconds 300
}
function Tab($parent, [string]$name) {
    $e = Find $parent $name "TabItem"
    try { $e.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select() }
    catch { $e.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }
    Start-Sleep -Milliseconds $SettleMs
}
function Step([string]$what, [scriptblock]$body) { try { & $body } catch { Write-Host "   step failed: $what -> $($_.Exception.Message)" } }

$finished = $false
try {
Write-Host "3. drive the sample web app"
Front
$main = Top "Sample Web ERP*"
Step "login" { SetVal $main "txtUser" "bob"; SetVal $main "txtPassword" "secret"; Act $main "Sign in" }
Front
Step "tab Customer Master" { Tab $main "Customer Master" }
# validation dialog: a short code
Step "short code" { SetVal $main "customerCode" "AB" "customerName" }
Step "save (invalid)" { Act $main "Save" }
Start-Sleep -Milliseconds 1200
Step "ok validation" { Act $main "OK" }
# the program fills the name when a known code is typed and the field loses focus
Step "known code" { SetVal $main "customerCode" "C001" "customerName" }
Start-Sleep -Milliseconds 800
Step "save (ok)" { Act $main "Save" }
Start-Sleep -Milliseconds 1200
Step "ok saved" { Act $main "OK" }
Step "other code" { SetVal $main "customerCode" "C002" "customerName" }
Start-Sleep -Milliseconds 800
# New Customer modal dialog
Step "new customer" { Act $main "New Customer" }
Start-Sleep -Milliseconds 1000
Step "new fields" { SetVal $main "newCode" "ZZTEST-CUS-002"; SetVal $main "newName" "ZZTEST Customer Two" }
Step "new ok" { Act $main "newOk" }
Start-Sleep -Milliseconds 1200
Step "created ok" { Act $main "btnMessageOk" }
Step "new customer again" { Act $main "New Customer" }
Start-Sleep -Milliseconds 800
Step "new cancel" { Act $main "Cancel" }
# Orders: input cells, computed line total, popup list
Step "tab Orders" { Tab $main "Orders" }
Start-Sleep -Milliseconds 800
foreach ($pair in @(@("2", "10"), @("3", "10"), @("3", "5"), @("4", "5"), @("7", "2.5"))) {
    Step "line values" {
        $q = Find $main "Quantity" "Edit"; $u = Find $main "Unit price" "Edit"
        foreach ($c in @(@($q, $pair[0]), @($u, $pair[1]))) { $c[0].SetFocus(); Start-Sleep -Milliseconds 150; $c[0].GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($c[1]); Start-Sleep -Milliseconds 500 }
    }
}
Step "add line" { Act $main "Add Line" }
Step "product 1 char" { SetVal $main "Product search" "W" }
Step "product 2 chars (popup list)" { SetVal $main "Product search" "Wi" }
Start-Sleep -Milliseconds 800
Step "pick product" { $o = Find $main "Widget Pro" "ListItem"; try { $o.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select() } catch { $o.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }; Start-Sleep -Milliseconds $SettleMs }
Step "tab Quote" { Tab $main "Quote" }
Start-Sleep -Milliseconds 800
Step "tab Customer Master" { Tab $main "Customer Master" }
Start-Sleep -Milliseconds 1000
# Exit closes the host window by itself
Step "exit" { Act $main "Exit" }
Start-Sleep -Milliseconds 1500

Write-Host "4. stop the sample app if still running (it is ours), learn, stop the watcher"
try { $app.Refresh(); if (-not $app.HasExited) { Stop-Process -Id $app.Id -Force } } catch { }
Start-Sleep -Milliseconds 5000
$code = Run-Watcher @("--learn")
Write-Host "   learn exit code $code"
Run-Watcher @("--expect") | Out-Null
Stop-Process -Id $watch.Id -Force
$finished = $true
} finally {
    if (-not $finished) { foreach ($p in @($app, $watch)) { try { Stop-Process -Id $p.Id -Force -ErrorAction Stop } catch { } } }
}

Write-Host "5. results"
$data = (Get-ChildItem (Join-Path $dataRoot "data") -Directory | Select-Object -First 1).FullName
$events = Get-ChildItem (Join-Path $data "events") -Filter *.jsonl -ErrorAction SilentlyContinue | ForEach-Object { Get-Content $_.FullName -Encoding UTF8 } | ForEach-Object { $_ | ConvertFrom-Json }
Write-Host ("   events: {0}" -f @($events).Count)
$events | Group-Object act | Sort-Object Count -Descending | ForEach-Object { Write-Host ("     {0,-11} {1}" -f $_.Name, $_.Count) }
Write-Host "   presses seen:"
$events | Where-Object { $_.act -eq "invoke" } | ForEach-Object { Write-Host ("     {0}  {1,-14} {2,-9} {3}" -f $_.t.Substring(11, 8), $_.name, ($_.how, "uia")[[string]::IsNullOrEmpty($_.how)], $_.win) }
$screens = @(Get-ChildItem (Join-Path $data "screens") -Filter *.md -ErrorAction SilentlyContinue)
Write-Host "   screens:"
$screens | ForEach-Object { $k = (Select-String -Path $_.FullName -Pattern '^\*\*Kind:\*\* (.*)').Matches; Write-Host ("     {0}  ({1})" -f $_.Name, $(if ($k.Count) { $k[0].Groups[1].Value } else { "?" })) }
Write-Host "   collector-status.json:"
Get-Content (Join-Path $data "collector-status.json") | Write-Host
$logicPath = Join-Path $data "memory\logic.md"
$logic = if (Test-Path $logicPath) { Get-Content $logicPath -Encoding UTF8 } else { @() }
Write-Host "   logic.md (what a trainee would know):"
$logic | Where-Object { $_ -match '^## |fills|=|opens|\?' } | Select-Object -First 40 | ForEach-Object { Write-Host ("     " + $_) }

# ---- summary ----
$presses = @($events | Where-Object { $_.act -eq "invoke" }).Count
$typed = @($events | Where-Object { $_.act -eq "value" -and $_.by -ne "app" }).Count
$dialogs = @($events | Where-Object { $_.act -eq "dialog" }).Count
$tabSel = @($events | Where-Object { $_.act -eq "select" }).Count
$formulas = @($logic | Where-Object { $_ -match "Line total" }).Count
$fillsLearned = @($logic | Where-Object { $_ -match "Typing Customer code fills" }).Count -ge 1
$checks = [ordered]@{
    "screens recorded (>= 3)"              = @($screens).Count
    "presses (>= 8)"                       = $presses
    "typed values (>= 8)"                  = $typed
    "dialogs (>= 2)"                       = $dialogs
    "tab selections (>= 2)"                = $tabSel
    "'Line total' lines in logic.md (>= 1)" = $formulas
}
$limits = @(3, 8, 8, 2, 2, 1)
Write-Host "6. summary"
$fail = 0; $i = 0
foreach ($k in $checks.Keys) {
    $ok = $checks[$k] -ge $limits[$i]; $i++
    if (-not $ok) { $fail++ }
    Write-Host ("   {0}  {1,-40} {2}" -f $(if ($ok) { "PASS" } else { "FAIL" }), $k, $checks[$k])
}
if ($fillsLearned) { Write-Host "   PASS  'Typing Customer code fills' learned" } else { $fail++; Write-Host "   FAIL  'Typing Customer code fills' not found in memory\logic.md" }
Write-Host ("   {0}: screens {1}, presses {2}, typed {3}, dialogs {4}, formulas {5}, fills learned {6}" -f $(if ($fail -eq 0) { "PASS" } else { "FAIL ($fail check(s))" }), @($screens).Count, $presses, $typed, $dialogs, $formulas, $fillsLearned)
Write-Host ("   data folder: {0}" -f $data)
if ($fail -ne 0) { exit 1 }
