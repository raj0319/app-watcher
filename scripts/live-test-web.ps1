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
Add-Type -AssemblyName System.Windows.Forms
Add-Type -Namespace LiveTest -Name Msg -MemberDefinition @'
[DllImport("user32.dll")] public static extern bool SetForegroundWindow(System.IntPtr h);
[DllImport("user32.dll")] public static extern System.IntPtr GetForegroundWindow();
public delegate bool EnumProc(System.IntPtr h, System.IntPtr l);
[DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc p, System.IntPtr l);
[DllImport("user32.dll")] public static extern bool IsWindowVisible(System.IntPtr h);
[DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(System.IntPtr h, out uint pid);
public static System.Collections.Generic.List<System.IntPtr> Visible(uint pid) { var r = new System.Collections.Generic.List<System.IntPtr>(); EnumWindows((h, l) => { uint p; GetWindowThreadProcessId(h, out p); if (p == pid && IsWindowVisible(h)) r.Add(h); return true; }, System.IntPtr.Zero); return r; }
'@

Add-Type -AssemblyName System.Drawing
Add-Type -Namespace LiveTest -Name Mouse -MemberDefinition @'
[DllImport("user32.dll")] public static extern bool GetCursorPos(out System.Drawing.Point p);
[DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
[DllImport("user32.dll")] public static extern void mouse_event(uint f, int x, int y, uint d, System.UIntPtr e);
[DllImport("user32.dll")] public static extern bool GetWindowRect(System.IntPtr h, out System.Drawing.Rectangle r);
'@ -ReferencedAssemblies System.Drawing

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
# real keystrokes (SendKeys), only while a window of the sample is in front; otherwise the value is set by pattern.
# Pattern driving presses no key, and a watcher that never sees a key cannot tell what the user typed from what the page
# filled in; this is what a real user does (type, then Tab out).
function TypeReal($parent, [string]$key, [string]$text, [bool]$tabOut = $false) {
    $e = Find $parent $key "Edit"
    $e.SetFocus(); Start-Sleep -Milliseconds 250
    if ([LiveTest.Msg]::Visible([uint32]$app.Id) -notcontains [LiveTest.Msg]::GetForegroundWindow()) {
        Write-Host "   (no real keys for $key - the sample is not in front; set by pattern)"; SetVal $parent $key $text $(if ($tabOut) { "customerName" } else { "" }); return
    }
    [System.Windows.Forms.SendKeys]::SendWait("^a")
    foreach ($ch in $text.ToCharArray()) { [System.Windows.Forms.SendKeys]::SendWait($(if ("+^%~(){}[]".Contains($ch)) { "{" + $ch + "}" } else { [string]$ch })); Start-Sleep -Milliseconds 60 }
    Start-Sleep -Milliseconds 400
    if ($tabOut) { [System.Windows.Forms.SendKeys]::SendWait("{TAB}"); Start-Sleep -Milliseconds 900 }
}
# a real mouse click (the way people press), only inside the sample's window and only while it is in front; otherwise the
# button is pressed by pattern. The watcher hears real clicks through its own button read; pattern presses rely on Chromium's Invoke event.
$script:Inconclusive = @()
function Click($parent, [string]$key, [string]$ct = "Button") {
    $e = Find $parent $key $ct
    $r = $e.Current.BoundingRectangle
    $h = [System.IntPtr]$app.MainWindowHandle; $app.Refresh(); $h = $app.MainWindowHandle
    $wr = New-Object System.Drawing.Rectangle; [LiveTest.Mouse]::GetWindowRect($h, [ref]$wr) | Out-Null
    $x = [int]($r.X + $r.Width / 2); $y = [int]($r.Y + $r.Height / 2)
    $inside = $x -ge $wr.X -and $x -le $wr.Right -and $y -ge $wr.Y -and $y -le $wr.Bottom
    if (-not $inside -or [LiveTest.Msg]::Visible([uint32]$app.Id) -notcontains [LiveTest.Msg]::GetForegroundWindow()) { $script:Inconclusive += $key; Write-Host "   (no real click on $key - the sample is not in front; pressed by pattern)"; Act $parent $key $ct; return }
    $sw = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds.Width; $sh = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds.Height
    $p = New-Object System.Drawing.Point; [LiveTest.Mouse]::GetCursorPos([ref]$p) | Out-Null
    foreach ($i in 1..3) { [LiveTest.Mouse]::mouse_event(0x8001, [int](65535 * ($p.X + ($x - $p.X) * $i / 3) / $sw), [int](65535 * ($p.Y + ($y - $p.Y) * $i / 3) / $sh), 0, [System.UIntPtr]::Zero); Start-Sleep -Milliseconds 60 }
    Start-Sleep -Milliseconds 150
    [LiveTest.Mouse]::mouse_event(0x0002, 0, 0, 0, [System.UIntPtr]::Zero); Start-Sleep -Milliseconds 80; [LiveTest.Mouse]::mouse_event(0x0004, 0, 0, 0, [System.UIntPtr]::Zero)
    Start-Sleep -Milliseconds $SettleMs
}
function Step([string]$what, [scriptblock]$body) { try { & $body } catch { Write-Host "   step failed: $what -> $($_.Exception.Message)" } }

$finished = $false
try {
Write-Host "3. drive the sample web app"
Front
$main = Top "Sample Web ERP*"
Step "login" { SetVal $main "txtUser" "bob"; SetVal $main "txtPassword" "secret"; Click $main "btnLogin" }
Front
Step "tab Customer Master" { Tab $main "Customer Master" }
# validation dialog: a short code
Step "short code" { SetVal $main "customerCode" "AB" "customerName" }
Step "save (invalid)" { Click $main "btnSaveCustomer" }
Start-Sleep -Milliseconds 1200
Step "ok validation" { Click $main "btnMessageOk" }
# the program fills the name when a known code is typed and the field loses focus
Step "known code" { TypeReal $main "customerCode" "C001" $true }
Start-Sleep -Milliseconds 800
Step "save (ok)" { Click $main "btnSaveCustomer" }
Start-Sleep -Milliseconds 1200
Step "ok saved" { Click $main "btnMessageOk" }
Step "other code" { TypeReal $main "customerCode" "C002" $true }
Start-Sleep -Milliseconds 800
# New Customer modal dialog
Step "new customer" { Click $main "btnNewCustomer" }
Start-Sleep -Milliseconds 1000
Step "new fields" { SetVal $main "newCode" "ZZTEST-CUS-002"; SetVal $main "newName" "ZZTEST Customer Two" }
Step "new ok" { Click $main "btnNewOk" }
Start-Sleep -Milliseconds 1200
Step "created ok" { Click $main "btnMessageOk" }
Step "new customer again" { Click $main "btnNewCustomer" }
Start-Sleep -Milliseconds 800
Step "new cancel" { Click $main "btnNewCancel" }
# Orders: input cells, computed line total, popup list
Step "tab Orders" { Tab $main "Orders" }
Start-Sleep -Milliseconds 800
foreach ($pair in @(@("2", "10"), @("3", "10"), @("3", "5"), @("4", "5"), @("7", "2.5"))) {
    Step "line values" {
        # real keys, as a user types them (a value set through UI Automation is rightly the program's, not typed)
        TypeReal $main "Quantity" $pair[0] $true; TypeReal $main "Unit price" $pair[1] $true
    }
}
Step "add line" { Act $main "Add Line" }
Step "product 1 char" { TypeReal $main "productSearch" "W" }
Step "product 2 chars (popup list)" { TypeReal $main "productSearch" "Wi" }
Start-Sleep -Milliseconds 800
Step "pick product" { $o = Find $main "Widget Pro" "ListItem"; try { $o.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select() } catch { $o.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }; Start-Sleep -Milliseconds $SettleMs }
Step "tab Quote" { Tab $main "Quote" }
Start-Sleep -Milliseconds 800
Step "tab Customer Master" { Tab $main "Customer Master" }
Start-Sleep -Milliseconds 1000
# Exit closes the host window by itself
Step "exit" { Click $main "btnExit" }
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
    "screens recorded (>= 1)"              = @($screens).Count
    "presses (>= 8)"                       = $presses
    "typed values (>= 8)"                  = $typed
    "dialogs (>= 2)"                       = $dialogs
    "tab selections (>= 2)"                = $tabSel
    "'Line total' lines in logic.md (>= 1)" = $formulas
}
# one page in one window: login, tabs and dialogs are views of the same window, so one screen file is the right answer for a window-based recorder
$limits = @(1, 8, 8, 2, 2, 1)
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


