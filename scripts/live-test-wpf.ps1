<#
.SYNOPSIS
  End-to-end test of the collector on a WPF program: starts App Watcher in portable mode on the
  SampleWpf app (tests\SampleWpf), drives that one window by UI Automation patterns (no global key
  presses, only that process; a few real mouse clicks only while the sample is in front), then learns and
  prints a short PASS/FAIL summary. Kills only the processes it started. Modelled on scripts\live-test.ps1.

.EXAMPLE
  dotnet build tests\SampleWpf
  powershell -ExecutionPolicy Bypass -File scripts\live-test-wpf.ps1
  powershell -ExecutionPolicy Bypass -File scripts\live-test-wpf.ps1 -Watcher .\publish\AppWatcher.exe
#>
param(
    [string]$Watcher = "",
    [string]$Sample = "",
    [int]$SettleMs = 900
)
$ErrorActionPreference = "Stop"
$root = Resolve-Path (Join-Path $PSScriptRoot "..")
if ($Watcher -eq "") {
    $w = Get-ChildItem (Join-Path $root "src\AppWatcher\bin\Debug") -Recurse -Filter AppWatcher.exe -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if ($null -ne $w) { $Watcher = $w.FullName }
}
if ($Sample -eq "") { $Sample = Join-Path $root "tests\SampleWpf\bin\Debug\net8.0-windows\SampleWpf.exe" }
if (-not (Test-Path $Watcher)) { throw "watcher not built: $Watcher" }
if (-not (Test-Path $Sample)) { throw "sample app not built: $Sample (dotnet build tests\SampleWpf)" }
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName Microsoft.VisualBasic
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
Add-Type -Namespace LiveTest -Name Msg -MemberDefinition @'
public delegate bool EnumProc(System.IntPtr h, System.IntPtr l);
[DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc p, System.IntPtr l);
[DllImport("user32.dll")] public static extern bool IsWindowVisible(System.IntPtr h);
[DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(System.IntPtr h, out uint pid);
public static System.Collections.Generic.List<System.IntPtr> Visible(uint pid) { var r = new System.Collections.Generic.List<System.IntPtr>(); EnumWindows((h, l) => { uint p; GetWindowThreadProcessId(h, out p); if (p == pid && IsWindowVisible(h)) r.Add(h); return true; }, System.IntPtr.Zero); return r; }
'@
Add-Type -Namespace LiveTest -Name Mouse -MemberDefinition @'
[DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
[DllImport("user32.dll")] public static extern bool GetCursorPos(out System.Drawing.Point p);
[DllImport("user32.dll")] public static extern void mouse_event(uint f, int x, int y, uint d, System.UIntPtr e);
[DllImport("user32.dll")] public static extern System.IntPtr GetForegroundWindow();
[DllImport("user32.dll")] public static extern bool GetWindowRect(System.IntPtr h, out System.Drawing.Rectangle r);
'@ -ReferencedAssemblies System.Drawing

if (-not $env:DOTNET_ROOT -and (Test-Path (Join-Path $env:USERPROFILE ".dotnet\dotnet.exe"))) { $env:DOTNET_ROOT = Join-Path $env:USERPROFILE ".dotnet" }
$dataRoot = Join-Path (Split-Path $Watcher) "AppWatcherData"
if (Test-Path $dataRoot) { Remove-Item $dataRoot -Recurse -Force }
Remove-Item (Join-Path (Split-Path $Sample) "sample-wpf-data.json") -ErrorAction SilentlyContinue

function Run-Watcher([string[]]$cmdArgs) {
    $p = Start-Process -FilePath $Watcher -ArgumentList (@("--portable") + $cmdArgs) -Wait -PassThru -NoNewWindow
    return $p.ExitCode
}

Write-Host "1. headless setup"
$code = Run-Watcher @("--setup", "`"$Sample`"", "test1234")
if ($code -ne 0) { throw "setup failed ($code)" }
$cfgPath = Join-Path $dataRoot "config.json"
$cfg = Get-Content $cfgPath -Raw | ConvertFrom-Json
Write-Host ("   toolkit: {0}, profile: {1}" -f $cfg.toolkit, $cfg.profile)
# the sample's JSON file is its database: add it as a data source so effects are captured
$cfg.dataSources = @(@{ kind = "file"; path = (Join-Path (Split-Path $Sample) "sample-wpf-data.json"); label = "sample-wpf-data.json"; enabled = $true })
$cfg.snapshot.afterDelayMs = 800
# the automatic file-access detection asks Windows for administrator permission; an unattended test must not raise that prompt
$cfg.autoDetectPending = $false
$cfg | ConvertTo-Json -Depth 10 | Set-Content $cfgPath -Encoding utf8

Write-Host "2. start watcher (tray) and sample app"
$watch = Start-Process -FilePath $Watcher -ArgumentList "--portable" -PassThru
Start-Sleep -Milliseconds 1500
$app = Start-Process -FilePath $Sample -ArgumentList "--corner" -PassThru
Start-Sleep -Milliseconds 2000

function Top([string]$title, [int]$tries = 40) {
    $pidCond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $app.Id)
    $seen = @()
    foreach ($i in 1..$tries) {
        $wins = [System.Windows.Automation.AutomationElement]::RootElement.FindAll([System.Windows.Automation.TreeScope]::Children, $pidCond)
        foreach ($w in $wins) { $n = $w.Current.Name; if ($n -like $title) { return $w }; if ($seen -notcontains $n) { $seen += $n } }
        try { $app.Refresh(); if ($app.MainWindowHandle -ne 0) { $h = [System.Windows.Automation.AutomationElement]::FromHandle($app.MainWindowHandle); if ($h.Current.Name -like $title) { return $h } } } catch { }
        foreach ($hw in [LiveTest.Msg]::Visible([uint32]$app.Id)) { try { $e = [System.Windows.Automation.AutomationElement]::FromHandle($hw); if ($e.Current.Name -like $title) { return $e } } catch { } }
        Start-Sleep -Milliseconds 250
    }
    throw "window not found: $title (windows of the app: $($seen -join ', '))"
}
function Find($parent, [string]$automationId) {
    foreach ($i in 1..10) {
        foreach ($d in $parent.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)) {
            if ($d.Current.AutomationId -eq $automationId) { return $d }
        }
        Start-Sleep -Milliseconds 300
    }
    $ids = @(); foreach ($d in $parent.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)) { $ids += $d.Current.AutomationId }
    throw "control not found: $automationId in [$($parent.Current.Name)] (ids: $($ids -join ', '))"
}
function Act($parent, [string]$automationId) {
    # WPF buttons have no window handle of their own: press through the Invoke pattern (the sample opens its dialog
    # through the dispatcher, so the call returns at once)
    $e = Find $parent $automationId
    $e.SetFocus(); Start-Sleep -Milliseconds 150
    try { $e.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() } catch { if ($_.Exception.Message -notlike "*timed out*") { throw } }
    Start-Sleep -Milliseconds $SettleMs
}
function SetVal($parent, [string]$automationId, [string]$value) {
    $e = Find $parent $automationId
    $e.SetFocus(); Start-Sleep -Milliseconds 120
    $e.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($value)
    Start-Sleep -Milliseconds 400
}
function FocusOn($parent, [string]$automationId) {
    # the focus leaves the field typed before: this is when a program fills a related field
    $e = Find $parent $automationId
    $e.SetFocus(); Start-Sleep -Milliseconds 700
}
function Menu($parent, [string]$top, [string]$itemName) {
    $m = $null
    foreach ($d in $parent.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)) {
        if ($d.Current.ControlType.ProgrammaticName -eq "ControlType.MenuItem" -and $d.Current.Name -eq $top) { $m = $d; break }
    }
    if ($null -eq $m) { throw "menu not found: $top" }
    $m.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
    Start-Sleep -Milliseconds 500
    $item = $null
    $pidCond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $app.Id)
    foreach ($i in 1..20) {
        $roots = @($parent) + @([System.Windows.Automation.AutomationElement]::RootElement.FindAll([System.Windows.Automation.TreeScope]::Children, $pidCond))
        foreach ($r in $roots) {
            foreach ($d in $r.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)) {
                if ($d.Current.ControlType.ProgrammaticName -eq "ControlType.MenuItem" -and $d.Current.Name -eq $itemName) { $item = $d; break }
            }
            if ($null -ne $item) { break }
        }
        if ($null -ne $item) { break }
        Start-Sleep -Milliseconds 200
    }
    if ($null -eq $item) { throw "menu item not found: $itemName" }
    try { $item.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() } catch { if ($_.Exception.Message -notlike "*timed out*") { throw } }
    Start-Sleep -Milliseconds $SettleMs
}
function Tab($parent, [string]$name) {
    # tabs open from the View menu like a real ERP; if the menu cannot be driven, select the TabItem by its pattern
    try { Menu $parent "View" $name }
    catch {
        $t = Find $parent ("tab" + @{ "Customer Master" = "Customers"; "Orders" = "Orders"; "Quote" = "Quote" }[$name])
        $t.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
        Start-Sleep -Milliseconds $SettleMs
        Write-Host "   (menu View > $name not found; selected the tab item by pattern)"
    }
}
function Front() {
    try { [Microsoft.VisualBasic.Interaction]::AppActivate($app.Id) } catch { }
    Start-Sleep -Milliseconds 400
}
# ---- real mouse input: only inside the window this script started, only while it is in front ----
$script:Inconclusive = @()
function MoveTo([int]$x, [int]$y) {
    $p = New-Object System.Drawing.Point; [LiveTest.Mouse]::GetCursorPos([ref]$p) | Out-Null
    $sw = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds.Width; $sh = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds.Height
    foreach ($i in 1..3) {
        $cx = $p.X + ($x - $p.X) * $i / 3; $cy = $p.Y + ($y - $p.Y) * $i / 3
        [LiveTest.Mouse]::mouse_event(0x8001, [int](65535 * $cx / $sw), [int](65535 * $cy / $sh), 0, [System.UIntPtr]::Zero)
        Start-Sleep -Milliseconds 60
    }
}
function RealClick($parent, [string]$automationId, [string]$what) {
    $e = Find $parent $automationId
    $r = $e.Current.BoundingRectangle
    $app.Refresh(); $h = $app.MainWindowHandle
    $wr = New-Object System.Drawing.Rectangle; [LiveTest.Mouse]::GetWindowRect($h, [ref]$wr) | Out-Null
    $x = [int]($r.X + $r.Width / 2); $y = [int]($r.Y + $r.Height / 2)
    $inside = $x -ge $wr.X -and $x -le $wr.Right -and $y -ge $wr.Y -and $y -le $wr.Bottom
    if ([LiveTest.Mouse]::GetForegroundWindow() -ne $h -or -not $inside) { $script:Inconclusive += $what; Write-Host "   (skipped the real click on $what - the sample is not in front, or the control is outside it)"; return $false }
    MoveTo $x $y
    Start-Sleep -Milliseconds 120
    if ([LiveTest.Mouse]::GetForegroundWindow() -ne $h) { $script:Inconclusive += $what; Write-Host "   (skipped the real click on $what - another window came in front)"; return $false }
    [LiveTest.Mouse]::mouse_event(0x0002, 0, 0, 0, [System.UIntPtr]::Zero); Start-Sleep -Milliseconds 60; [LiveTest.Mouse]::mouse_event(0x0004, 0, 0, 0, [System.UIntPtr]::Zero)
    Start-Sleep -Milliseconds $SettleMs
    return $true
}
# real keystrokes (SendKeys) into the field, only while a window of the sample is in front; falls back to the pattern otherwise.
# Pattern driving presses no key, and a watcher that never sees a key cannot tell what the user typed from what the program
# filled in; this is what a real user does (type, then Tab out).
function TypeReal($parent, [string]$automationId, [string]$text, [bool]$tabOut = $false) {
    $e = Find $parent $automationId
    $e.SetFocus(); Start-Sleep -Milliseconds 200
    if ([LiveTest.Msg]::Visible([uint32]$app.Id) -notcontains [LiveTest.Mouse]::GetForegroundWindow()) {
        $script:Inconclusive += "keys in $automationId"; Write-Host "   (no real keys for $automationId - the sample is not in front; set by pattern)"; SetVal $parent $automationId $text; return
    }
    [System.Windows.Forms.SendKeys]::SendWait("^a")
    foreach ($ch in $text.ToCharArray()) { [System.Windows.Forms.SendKeys]::SendWait($(if ("+^%~(){}[]".Contains($ch)) { "{" + $ch + "}" } else { [string]$ch })); Start-Sleep -Milliseconds 60 }
    Start-Sleep -Milliseconds 400
    if ($tabOut) { [System.Windows.Forms.SendKeys]::SendWait("{TAB}"); Start-Sleep -Milliseconds 800 }
}
function Step([string]$what, [scriptblock]$do) {
    try { & $do } catch { Write-Host "   step failed: $what -> $($_.Exception.Message)" }
}

$finished = $false
$cursorBefore = New-Object System.Drawing.Point; [LiveTest.Mouse]::GetCursorPos([ref]$cursorBefore) | Out-Null
try {
Write-Host "3. drive the sample app"
Front
$login = $null
try { $login = Top "Sample WPF ERP - Login" 20 } catch { Write-Host "   (login window not found; someone may have passed it already: $_)" }
if ($null -ne $login) {
    Step "user" { SetVal $login "txtUser" "bob" }
    # a PasswordBox exposes no value pattern to UI Automation: nothing is typed there (the watcher would mask it anyway)
    Step "password" { SetVal $login "txtPassword" "secret" }
    Step "log in" { Act $login "btnLogin" }
}
$main = Top "Sample WPF ERP"
Front
Step "tab Customer Master" { Tab $main "Customer Master" }
Start-Sleep -Milliseconds 800
# a code that is too short: the validation message
Step "short code" { SetVal $main "txtCustomerCode" "AB" }
Step "save (invalid)" { Act $main "btnSaveCustomer" }
Start-Sleep -Milliseconds 2500
# a known code: the program fills the name when the focus leaves the code box
Step "code C001" { TypeReal $main "txtCustomerCode" "C001" $true }
Step "save (valid)" { Act $main "btnSaveCustomer" }
Start-Sleep -Milliseconds 2500
Step "code C002" { TypeReal $main "txtCustomerCode" "C002" $true }
Step "code ZZTEST-CUS-001" { SetVal $main "txtCustomerCode" "ZZTEST-CUS-001" }
Step "name" { SetVal $main "txtCustomerName" "ZZTEST Customer One" }
Step "save (valid)" { Act $main "btnSaveCustomer" }
Start-Sleep -Milliseconds 2500
Front
# the New Customer dialog: once through the menu with OK, once through the button with Cancel
$dlg = $null
Step "File > New Customer" { Menu $main "File" "New Customer..." }
try { $dlg = Top "New Customer" 20 } catch { Write-Host "   step failed: $_" }
if ($null -ne $dlg) {
    Front
    Step "new code" { SetVal $dlg "txtNewCode" "ZZTEST-CUS-002" }
    Step "new name" { SetVal $dlg "txtNewName" "ZZTEST Customer Two" }
    Step "dialog OK" { Act $dlg "btnNewOk" }
}
Start-Sleep -Milliseconds 800
Step "tab Customer Master" { Tab $main "Customer Master" }
Step "save (valid)" { Act $main "btnSaveCustomer" }
Start-Sleep -Milliseconds 2500
Step "New... button" { Act $main "btnNewCustomer" }
$dlg2 = $null
try { $dlg2 = Top "New Customer" 20 } catch { Write-Host "   step failed: $_" }
if ($null -ne $dlg2) {
    Front
    Step "new code" { SetVal $dlg2 "txtNewCode" "ZZTEST-CUS-003" }
    Step "dialog Cancel" { Act $dlg2 "btnNewCancel" }
}
Start-Sleep -Milliseconds 800
# Orders: lookup, grid lines, the program calculates Line total = Quantity x Unit price
Step "tab Orders" { Tab $main "Orders" }
Start-Sleep -Milliseconds 800
Step "lookup Wi" { SetVal $main "cboProduct" "Wi"; Start-Sleep -Milliseconds 800; try { (Find $main "cboProduct").GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Collapse() } catch { } }
Step "add line 1" { Act $main "btnAddLine" }
Step "lookup Ga" { SetVal $main "cboProduct" "Ga"; Start-Sleep -Milliseconds 800; try { (Find $main "cboProduct").GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Collapse() } catch { } }
Step "add line 2" { Act $main "btnAddLine" }
foreach ($s in @(@("txtQty_1", "4"), @("txtPrice_1", "12.50"), @("txtQty_1", "3"), @("txtQty_2", "10"), @("txtPrice_2", "7"), @("txtPrice_1", "20"), @("txtQty_2", "6"))) {
    Step "grid $($s[0])" { TypeReal $main $s[0] $s[1] $true; Start-Sleep -Milliseconds 500 }
}
Start-Sleep -Milliseconds 800
# Quote tab: a button that does nothing, pressed through the pattern and once with the real mouse
Step "tab Quote" { Tab $main "Quote" }
Start-Sleep -Milliseconds 800
Step "Print quote" { Act $main "btnPrintQuote" }
Step "Help > About" { Menu $main "Help" "About" }
Start-Sleep -Milliseconds 2500
Front
Step "real click Print quote" { RealClick $main "btnPrintQuote" "Print quote" | Out-Null }
Start-Sleep -Milliseconds 1500
Step "tab Customer Master" { Tab $main "Customer Master" }
Start-Sleep -Milliseconds 1000
# File > Exit would close the program through its own menu; the test ends it by killing the process it started instead
[LiveTest.Mouse]::SetCursorPos($cursorBefore.X, $cursorBefore.Y) | Out-Null

Write-Host "4. stop the sample app (killed: it is ours), learn, stop the watcher"
Stop-Process -Id $app.Id -Force
Start-Sleep -Milliseconds 5000
$code = Run-Watcher @("--learn")
Write-Host "   learn exit code $code"
Run-Watcher @("--expect") | Out-Null
Stop-Process -Id $watch.Id -Force

$finished = $true
} finally {
    if (-not $finished) { foreach ($p in @($app, $watch)) { try { Stop-Process -Id $p.Id -Force -ErrorAction Stop } catch { } } }
    try { [LiveTest.Mouse]::SetCursorPos($cursorBefore.X, $cursorBefore.Y) | Out-Null } catch { }
}

Write-Host "5. results"
$data = (Get-ChildItem (Join-Path $dataRoot "data") -Directory | Select-Object -First 1).FullName
$events = @(Get-ChildItem (Join-Path $data "events") -Filter *.jsonl | ForEach-Object { Get-Content $_.FullName } | ForEach-Object { $_ | ConvertFrom-Json })
Write-Host ("   events: {0}" -f $events.Count)
$events | Group-Object act | Sort-Object Count -Descending | ForEach-Object { Write-Host ("     {0,-11} {1}" -f $_.Name, $_.Count) }
Write-Host "   windows seen:"
$events | Group-Object win | Sort-Object Count -Descending | ForEach-Object { Write-Host ("     {0,3}  {1}" -f $_.Count, $_.Name) }
Write-Host "   presses seen:"
$events | Where-Object { $_.act -eq "invoke" } | ForEach-Object { Write-Host ("     {0}  {1,-14} {2,-9} {3}" -f $_.t.Substring(11, 8), $_.name, ($_.how, "uia")[[string]::IsNullOrEmpty($_.how)], $_.win) }
Write-Host "   collector-status.json:"
Get-Content (Join-Path $data "collector-status.json") | Write-Host
$logicPath = Join-Path $data "memory\logic.md"
Write-Host "   logic.md (what a trainee would know):"
if (Test-Path $logicPath) { Get-Content $logicPath -Encoding UTF8 | Where-Object { $_ -match '^## |fills|=|opens|Not used|nobody|\?' } | Select-Object -First 45 | ForEach-Object { Write-Host ("     " + $_) } }
Write-Host ("   data folder: {0}" -f $data)

# ---- short summary ----
$screens = @(Get-ChildItem (Join-Path $data "screens") -Filter *.md -ErrorAction SilentlyContinue).Count
$presses = @($events | Where-Object { $_.act -eq "invoke" }).Count
$typed = @($events | Where-Object { $_.act -eq "value" -and $_.by -ne "app" }).Count
$dialogs = @($events | Where-Object { $_.act -eq "dialog" }).Count
$logicText = if (Test-Path $logicPath) { Get-Content $logicPath -Raw -Encoding UTF8 } else { "" }
$formulas = @(($logicText -split "`n") | Where-Object { $_ -match "Line total" }).Count
$fills = $logicText -match "Typing Customer code fills"
$mark = { param($ok) if ($ok) { "PASS" } else { "FAIL" } }
Write-Host ""
Write-Host "SUMMARY (WPF live test)"
Write-Host ("   {0}  screens: {1} (need 3 or more: login, main/tabs, New Customer)" -f (& $mark ($screens -ge 3)), $screens)
Write-Host ("   {0}  presses: {1} (need 5 or more)" -f (& $mark ($presses -ge 5)), $presses)
Write-Host ("   {0}  typed values: {1} (need 5 or more)" -f (& $mark ($typed -ge 5)), $typed)
Write-Host ("   {0}  dialogs: {1} (need 1 or more)" -f (& $mark ($dialogs -ge 1)), $dialogs)
Write-Host ("   {0}  formula lines mentioning 'Line total' in logic.md: {1}" -f (& $mark ($formulas -ge 1)), $formulas)
Write-Host ("   {0}  'Typing Customer code fills' learned: {1}" -f (& $mark $fills), $fills)
if ($script:Inconclusive.Count -gt 0) { Write-Host ("   note: real clicks skipped: {0}" -f ($script:Inconclusive -join ", ")) }
$all = $screens -ge 3 -and $presses -ge 5 -and $typed -ge 5 -and $dialogs -ge 1 -and $formulas -ge 1 -and $fills
Write-Host ("   OVERALL: {0}" -f (& $mark $all))
