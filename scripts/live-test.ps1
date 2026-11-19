<#
.SYNOPSIS
  End-to-end test of the collector against real windows: starts App Watcher in portable mode on the
  SampleApp (tests\SampleApp), drives the sample app by UI Automation patterns (no global key presses,
  only that one process), then learns and prints what was recorded. Kills only the processes it started.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File scripts\live-test.ps1
  powershell -ExecutionPolicy Bypass -File scripts\live-test.ps1 -Watcher .\publish\AppWatcher.exe
#>
param(
    [string]$Watcher = "",
    [string]$Sample = "",
    [int]$SettleMs = 900,
    [switch]$InvokeButtons
)
$ErrorActionPreference = "Stop"
$root = Resolve-Path (Join-Path $PSScriptRoot "..")
if ($Watcher -eq "") {
    # the newest debug build (the folder name follows the target framework, which changed for the OCR API)
    $w = Get-ChildItem (Join-Path $root "src\AppWatcher\bin\Debug") -Recurse -Filter AppWatcher.exe -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if ($null -ne $w) { $Watcher = $w.FullName }
}
if ($Sample -eq "") { $Sample = Join-Path $root "tests\SampleApp\bin\Debug\net8.0-windows\SampleApp.exe" }
if (-not (Test-Path $Watcher)) { throw "watcher not built: $Watcher" }
if (-not (Test-Path $Sample)) { throw "sample app not built: $Sample (dotnet build tests\SampleApp)" }
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName Microsoft.VisualBasic
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
Add-Type -Namespace LiveTest -Name Msg -MemberDefinition @'
[DllImport("user32.dll")] public static extern bool PostMessage(System.IntPtr hWnd, uint msg, System.IntPtr w, System.IntPtr l);
public delegate bool EnumProc(System.IntPtr h, System.IntPtr l);
[DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc p, System.IntPtr l);
[DllImport("user32.dll")] public static extern bool IsWindowVisible(System.IntPtr h);
[DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(System.IntPtr h, out uint pid);
public static System.Collections.Generic.List<System.IntPtr> Visible(uint pid) { var r = new System.Collections.Generic.List<System.IntPtr>(); EnumWindows((h, l) => { uint p; GetWindowThreadProcessId(h, out p); if (p == pid && IsWindowVisible(h)) r.Add(h); return true; }, System.IntPtr.Zero); return r; }
'@
# real mouse input, used for a few clicks only, inside the sample app's own window, after checking that window is in front
Add-Type -Namespace LiveTest -Name Mouse -MemberDefinition @'
[DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
[DllImport("user32.dll")] public static extern bool GetCursorPos(out System.Drawing.Point p);
[DllImport("user32.dll")] public static extern void mouse_event(uint f, int x, int y, uint d, System.UIntPtr e);
[DllImport("user32.dll")] public static extern System.IntPtr GetForegroundWindow();
[DllImport("user32.dll")] public static extern bool GetWindowRect(System.IntPtr h, out System.Drawing.Rectangle r);
'@ -ReferencedAssemblies System.Drawing

# the sample app is framework-dependent; when the SDK lives in the user profile the apphost needs DOTNET_ROOT
if (-not $env:DOTNET_ROOT -and (Test-Path (Join-Path $env:USERPROFILE ".dotnet\dotnet.exe"))) { $env:DOTNET_ROOT = Join-Path $env:USERPROFILE ".dotnet" }
$dataRoot = Join-Path (Split-Path $Watcher) "AppWatcherData"
if (Test-Path $dataRoot) { Remove-Item $dataRoot -Recurse -Force }
Remove-Item (Join-Path (Split-Path $Sample) "sample-data.json") -ErrorAction SilentlyContinue

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
# the sample app's JSON file is its database: add it as a data source so effects are captured
$cfg.dataSources = @(@{ kind = "file"; path = (Join-Path (Split-Path $Sample) "sample-data.json"); label = "sample-data.json"; enabled = $true })
$cfg.snapshot.afterDelayMs = 800
# the automatic file-access detection asks Windows for administrator permission; an unattended test must not raise that prompt
$cfg.autoDetectPending = $false
Write-Host ("   recommended defaults: shots {0}, OCR {1}, mask titles {2}, tabs {3}, MSAA {4}, press inference {5}, keep {6} days, learn at {7}" -f `
    $cfg.privacy.screenshotMode, $cfg.privacy.readTextFromPictures, $cfg.privacy.maskTitles, $cfg.collector.tabDocuments, $cfg.collector.msaaFallback, $cfg.collector.pressInference, $cfg.retentionDays, $cfg.learnAt)
$cfg | ConvertTo-Json -Depth 10 | Set-Content $cfgPath -Encoding utf8

Write-Host "2. start watcher (tray) and sample app"
$watch = Start-Process -FilePath $Watcher -ArgumentList "--portable" -PassThru
Start-Sleep -Milliseconds 1500
$app = Start-Process -FilePath $Sample -ArgumentList "--corner" -PassThru
Start-Sleep -Milliseconds 2000

$auto = [System.Windows.Automation.AutomationElement]
$cond = { param($n) New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $n) }
function Top([string]$title, [int]$tries = 40) {
    $pidCond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $app.Id)
    $seen = @()
    foreach ($i in 1..$tries) {
        $wins = [System.Windows.Automation.AutomationElement]::RootElement.FindAll([System.Windows.Automation.TreeScope]::Children, $pidCond)
        foreach ($w in $wins) { $n = $w.Current.Name; if ($n -like $title) { return $w }; if ($seen -notcontains $n) { $seen += $n } }
        # a modal dialog owned by the main window is not always listed under the desktop: the process's main window handle points at it, and Win32 lists it
        try { $app.Refresh(); if ($app.MainWindowHandle -ne 0) { $h = [System.Windows.Automation.AutomationElement]::FromHandle($app.MainWindowHandle); if ($h.Current.Name -like $title) { return $h } } } catch { }
        foreach ($hw in [LiveTest.Msg]::Visible([uint32]$app.Id)) { try { $e = [System.Windows.Automation.AutomationElement]::FromHandle($hw); if ($e.Current.Name -like $title) { return $e } } catch { } }
        Start-Sleep -Milliseconds 250
    }
    throw "window not found: $title (windows of the app: $($seen -join ', '))"
}
function Find($parent, [string]$automationId) {
    # walk with a TrueCondition and filter here: property conditions evaluated by UIA on a WinForms TabControl can flip its selected tab
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
    # a press through the button's own window: a posted BM_CLICK (no global input, nothing waits on the click).
    # A posted mouse down/up does not work: a WinForms button only completes a click while it holds the mouse capture.
    # $InvokeButtons uses the UI Automation Invoke call instead, which skips the pressed state real clicks have.
    $e = Find $parent $automationId
    $h = [System.IntPtr]$e.Current.NativeWindowHandle
    if ($InvokeButtons -or $h -eq [System.IntPtr]::Zero) {
        $e.SetFocus(); Start-Sleep -Milliseconds 150
        try { $e.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() } catch { if ($_.Exception.Message -notlike "*timed out*") { throw } }
    } else {
        [LiveTest.Msg]::PostMessage($h, 0x00F5, [System.IntPtr]::Zero, [System.IntPtr]::Zero) | Out-Null   # BM_CLICK
    }
    Start-Sleep -Milliseconds $SettleMs
}
function SetVal($parent, [string]$automationId, [string]$value) {
    $e = Find $parent $automationId
    $e.SetFocus(); Start-Sleep -Milliseconds 120
    $e.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($value)
    Start-Sleep -Milliseconds 400
}
function Tab($parent, [string]$name) {
    # real ERPs open tab documents from a menu; the sample app has View > <tab>. (WinForms hides tab items from a
    # second UIA client while an event subscriber is attached, so selecting the TabItem directly is not reliable.)
    # When the menu cannot be driven, a TCM_SETCURFOCUS posted to the tab strip selects the tab (scoped to that window).
    try { Menu $parent "View" $name }
    catch {
        $index = @{ "Customer Master" = 0; "Orders" = 1; "Quote" = 2 }[$name]
        if ($null -eq $index) { throw }
        $strip = Find $parent "tabWorkspace"
        [LiveTest.Msg]::PostMessage([System.IntPtr]$strip.Current.NativeWindowHandle, 0x1330, [System.IntPtr]$index, [System.IntPtr]::Zero) | Out-Null
        Start-Sleep -Milliseconds $SettleMs
        Write-Host "   (menu View > $name not found; selected the tab through the tab strip)"
    }
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
        # the drop-down is a top-level window of the process; look inside each one (and inside the main window) for the item
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
    # a menu item that opens a modal window does not return until that window closes: the invoke still happened
    try { $item.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() } catch { if ($_.Exception.Message -notlike "*timed out*") { throw } }
    Start-Sleep -Milliseconds $SettleMs
}
function MenuDetached($parent, [string]$top, [string]$itemName) {
    # a menu item that opens a modal window: the Invoke call does not return until that window closes, so it runs detached
    $m = $null
    foreach ($d in $parent.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)) {
        if ($d.Current.ControlType.ProgrammaticName -eq "ControlType.MenuItem" -and $d.Current.Name -eq $top) { $m = $d; break }
    }
    if ($null -eq $m) { throw "menu not found: $top" }
    $m.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
    Start-Sleep -Milliseconds 500
    Start-Process -FilePath "powershell" -ArgumentList @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", "`"$PSScriptRoot\live-test-invoke.ps1`"", "-ProcessId", $app.Id, "-Name", "`"$itemName`"") -WindowStyle Hidden | Out-Null
    Start-Sleep -Milliseconds ($SettleMs * 2)
}
function PostClick($parent, [string]$automationId) {
    # a button that opens a blocking window: a UIA Invoke would stay pending until that window closes, and while it is
    # pending neither this script nor the watcher can read the new window. A posted BM_CLICK to that one button does not wait.
    $e = Find $parent $automationId
    $h = [System.IntPtr]$e.Current.NativeWindowHandle
    if ($h -eq [System.IntPtr]::Zero) { throw "no window handle for $automationId" }
    [LiveTest.Msg]::PostMessage($h, 0x00F5, [System.IntPtr]::Zero, [System.IntPtr]::Zero) | Out-Null
    Start-Sleep -Milliseconds ($SettleMs * 2)
}
function Front() {
    # the watcher records only while the app is in front; bring it there the way a user would (activate its window)
    try { [Microsoft.VisualBasic.Interaction]::AppActivate($app.Id) } catch { }
    Start-Sleep -Milliseconds 400
}
# ---- real mouse input: only on the window this script started, found by its handle; the pointer is put back afterwards ----
$script:Inconclusive = @()
function AppWindowRect($window = $null) {
    # the window the click must stay inside: the sample app's main window, or the given window of the same process (the workspace)
    if ($null -ne $window) { $h = [System.IntPtr]$window.Current.NativeWindowHandle } else { $app.Refresh(); $h = $app.MainWindowHandle }
    $r = New-Object System.Drawing.Rectangle; [LiveTest.Mouse]::GetWindowRect($h, [ref]$r) | Out-Null; return @{ Handle = $h; Rect = $r }
}
function FindByName($parent, [string]$name, [string]$controlType) {
    foreach ($i in 1..10) {
        foreach ($d in $parent.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)) {
            if ($d.Current.Name -eq $name -and $d.Current.ControlType.ProgrammaticName -eq "ControlType.$controlType") { return $d }
        }
        Start-Sleep -Milliseconds 300
    }
    throw "control not found: $controlType '$name' in [$($parent.Current.Name)]"
}
function MoveTo([int]$x, [int]$y) {
    # absolute moves in a few steps (a WinForms tip only shows after real mouse movement; SetCursorPos alone is not enough)
    $p = New-Object System.Drawing.Point; [LiveTest.Mouse]::GetCursorPos([ref]$p) | Out-Null
    $sw = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds.Width; $sh = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds.Height
    foreach ($i in 1..3) {
        $cx = $p.X + ($x - $p.X) * $i / 3; $cy = $p.Y + ($y - $p.Y) * $i / 3
        [LiveTest.Mouse]::mouse_event(0x8001, [int](65535 * $cx / $sw), [int](65535 * $cy / $sh), 0, [System.UIntPtr]::Zero)   # MOVE | ABSOLUTE
        Start-Sleep -Milliseconds 60
    }
}
function RealClick($parent, [string]$automationId, [string]$what, $window = $null, $element = $null) {
    $e = if ($null -ne $element) { $element } else { Find $parent $automationId }
    $r = $e.Current.BoundingRectangle
    $w = AppWindowRect $window
    $x = [int]($r.X + $r.Width / 2); $y = [int]($r.Y + $r.Height / 2)
    $inside = $x -ge $w.Rect.X -and $x -le $w.Rect.Right -and $y -ge $w.Rect.Y -and $y -le $w.Rect.Bottom
    if ([LiveTest.Mouse]::GetForegroundWindow() -ne $w.Handle -or -not $inside) { $script:Inconclusive += $what; Write-Host "   (skipped the real click on $what - the sample app is not the window in front, or the control is outside it)"; return $false }
    MoveTo $x $y
    Start-Sleep -Milliseconds 120
    if ([LiveTest.Mouse]::GetForegroundWindow() -ne $w.Handle) { $script:Inconclusive += $what; Write-Host "   (skipped the real click on $what - another window came in front)"; return $false }
    [LiveTest.Mouse]::mouse_event(0x0002, 0, 0, 0, [System.UIntPtr]::Zero); Start-Sleep -Milliseconds 60; [LiveTest.Mouse]::mouse_event(0x0004, 0, 0, 0, [System.UIntPtr]::Zero)
    Start-Sleep -Milliseconds $SettleMs
    return $true
}
function RealClickAt($parent, [string]$automationId, [int]$dx, [int]$dy, [string]$what, $window = $null) {
    # a real click at an offset inside a control (a tab page of a tab control), with the same window checks as RealClick
    $e = Find $parent $automationId
    $r = $e.Current.BoundingRectangle
    $w = AppWindowRect $window
    $x = [int]($r.X + $dx); $y = [int]($r.Y + $dy)
    $inside = $x -ge $w.Rect.X -and $x -le $w.Rect.Right -and $y -ge $w.Rect.Y -and $y -le $w.Rect.Bottom
    if ([LiveTest.Mouse]::GetForegroundWindow() -ne $w.Handle -or -not $inside) { $script:Inconclusive += $what; Write-Host "   (skipped the real click on $what - the sample app is not the window in front, or the control is outside it)"; return $false }
    MoveTo $x $y
    Start-Sleep -Milliseconds 120
    if ([LiveTest.Mouse]::GetForegroundWindow() -ne $w.Handle) { $script:Inconclusive += $what; Write-Host "   (skipped the real click on $what - another window came in front)"; return $false }
    [LiveTest.Mouse]::mouse_event(0x0002, 0, 0, 0, [System.UIntPtr]::Zero); Start-Sleep -Milliseconds 60; [LiveTest.Mouse]::mouse_event(0x0004, 0, 0, 0, [System.UIntPtr]::Zero)
    Start-Sleep -Milliseconds $SettleMs
    return $true
}
function RealHover($parent, [string]$automationId, [string]$what) {
    $e = Find $parent $automationId
    $r = $e.Current.BoundingRectangle
    $w = AppWindowRect
    if ([LiveTest.Mouse]::GetForegroundWindow() -ne $w.Handle) { $script:Inconclusive += $what; Write-Host "   (skipped the hover on $what - the sample app is not the window in front)"; return $false }
    # a tip only shows after the pointer arrives and rests: leave the control first (to the window's status bar), then come back
    MoveTo ([int]($w.Rect.X + $w.Rect.Width / 2)) ([int]($w.Rect.Bottom - 12))
    Start-Sleep -Milliseconds 400
    MoveTo ([int]($r.X + $r.Width / 2)) ([int]($r.Y + $r.Height / 2))
    Start-Sleep -Milliseconds 1200   # a tip shows after the pointer rests
    return $true
}

$finished = $false
$cursorBefore = New-Object System.Drawing.Point; [LiveTest.Mouse]::GetCursorPos([ref]$cursorBefore) | Out-Null
try {
Write-Host "3. drive the sample app"
Front
$login = $null
try { $login = Top "Sample ERP - Login" 20 } catch { Write-Host "   (login window not found; someone may have passed it already: $_)" }
if ($null -ne $login) {
try { SetVal $login "txtUser" "bob" } catch { Write-Host "   step failed: SetVal $login 'txtUser' 'bob' -> $($_.Exception.Message)" }
try { SetVal $login "txtPassword" "secret" } catch { Write-Host "   step failed: SetVal $login 'txtPassword' 'secret' -> $($_.Exception.Message)" }
try { Act $login "btnLogin" } catch { Write-Host "   step failed: Act $login 'btnLogin' -> $($_.Exception.Message)" }
}
$main = Top "Sample ERP"
Front
try { Tab $main "Customer Master" } catch { Write-Host "   step failed: Tab $main 'Customer Master' -> $($_.Exception.Message)" }
Start-Sleep -Milliseconds 800
try { Tab $main "Customer Master" } catch { Write-Host "   step failed: Tab $main 'Customer Master' -> $($_.Exception.Message)" }
try { SetVal $main "txtCustomerCode" "AB" } catch { Write-Host "   step failed: SetVal $main 'txtCustomerCode' 'AB' -> $($_.Exception.Message)" }
try { Act $main "btnSaveCustomer" } catch { Write-Host "   step failed: Act $main 'btnSaveCustomer' -> $($_.Exception.Message)" }
Start-Sleep -Milliseconds 2500
try { SetVal $main "txtCustomerCode" "ZZTEST-CUS-001" } catch { Write-Host "   step failed: SetVal $main 'txtCustomerCode' 'ZZTEST-CUS-001' -> $($_.Exception.Message)" }
try { SetVal $main "txtCustomerName" "ZZTEST Customer One" } catch { Write-Host "   step failed: SetVal $main 'txtCustomerName' 'ZZTEST Customer One' -> $($_.Exception.Message)" }
try { Act $main "btnSaveCustomer" } catch { Write-Host "   step failed: Act $main 'btnSaveCustomer' -> $($_.Exception.Message)" }
Start-Sleep -Milliseconds 2000
Front
try { PostClick $main "btnNewCustomer" } catch { Write-Host "   step failed: PostClick $main 'btnNewCustomer' -> $($_.Exception.Message)" }
try { $dlg = Top "New Customer" } catch { Write-Host "   step failed: $_"; $dlg = $main }
Front
try { SetVal $dlg "txtNewCode" "ZZTEST-CUS-002" } catch { Write-Host "   step failed: SetVal $dlg 'txtNewCode' 'ZZTEST-CUS-002' -> $($_.Exception.Message)" }
try { SetVal $dlg "txtNewName" "ZZTEST Customer Two" } catch { Write-Host "   step failed: SetVal $dlg 'txtNewName' 'ZZTEST Customer Two' -> $($_.Exception.Message)" }
try { Act $dlg "btnNewOk" } catch { Write-Host "   step failed: Act $dlg 'btnNewOk' -> $($_.Exception.Message)" }
try { Tab $main "Customer Master" } catch { Write-Host "   step failed: Tab $main 'Customer Master' -> $($_.Exception.Message)" }
try { Act $main "btnSaveCustomer" } catch { Write-Host "   step failed: Act $main 'btnSaveCustomer' -> $($_.Exception.Message)" }
Start-Sleep -Milliseconds 2000
try { Tab $main "Orders" } catch { Write-Host "   step failed: Tab $main 'Orders' -> $($_.Exception.Message)" }
Start-Sleep -Milliseconds 800
try { Tab $main "Orders" } catch { Write-Host "   step failed: Tab $main 'Orders' -> $($_.Exception.Message)" }
try { SetVal $main "neAmount" "abc" } catch { Write-Host "   step failed: SetVal $main 'neAmount' 'abc' -> $($_.Exception.Message)" }
try { Act $main "btnPost" } catch { Write-Host "   step failed: Act $main 'btnPost' -> $($_.Exception.Message)" }
Start-Sleep -Milliseconds 2500
try { SetVal $main "neAmount" "125.50" } catch { Write-Host "   step failed: SetVal $main 'neAmount' '125.50' -> $($_.Exception.Message)" }
try { $rush = Find $main "chkRush"; $rush.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle(); Start-Sleep -Milliseconds 500 } catch { Write-Host "   step failed: $rush = Find $main 'chkRush'; $rush.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle(); Start-Sleep -Milliseconds 500 -> $($_.Exception.Message)" }
try { Act $main "btnPost" } catch { Write-Host "   step failed: Act $main 'btnPost' -> $($_.Exception.Message)" }
Start-Sleep -Milliseconds 2000
try { SetVal $main "neAmount" "40" } catch { Write-Host "   step failed: SetVal $main 'neAmount' '40' -> $($_.Exception.Message)" }
try { Act $main "btnPost" } catch { Write-Host "   step failed: Act $main 'btnPost' -> $($_.Exception.Message)" }
Start-Sleep -Milliseconds 2000
# the Quote tab: the program fills the customer name and calculates the line total (what the logic report should explain)
try { Tab $main "Quote" } catch { Write-Host "   step failed: Tab $main 'Quote' -> $($_.Exception.Message)" }
Start-Sleep -Milliseconds 800
foreach ($step in @(@("txtQuoteCustomer", "ZZTEST-CUS-001"), @("txtDiscount", "10"), @("txtPrice", "12.50"), @("txtQty", "4"), @("txtQty", "3"),
                    @("txtPrice", "20"), @("txtQuoteCustomer", "ZZTEST-CUS-002"), @("txtDiscount", "5"), @("txtQuoteCustomer", "ZZTEST-NOBODY"), @("txtQty", "7"))) {
    try { SetVal $main $step[0] $step[1]; Start-Sleep -Milliseconds 500 } catch { Write-Host "   step failed: SetVal $main '$($step[0])' -> $($_.Exception.Message)" }
}
try { Menu $main "Help" "About" } catch { Write-Host "   step failed: Menu $main 'Help' 'About' -> $($_.Exception.Message)" }
Start-Sleep -Milliseconds 2500
# a few real clicks and one hover on the sample app's own window (the toolkit path: a button that raises no event and only changes the window)
Front
try { RealClick $main "btnRefresh" "Refresh" | Out-Null } catch { Write-Host "   step failed: RealClick Refresh -> $($_.Exception.Message)" }
Start-Sleep -Milliseconds 1500
try { RealClick $main "btnRefresh" "Refresh" | Out-Null } catch { Write-Host "   step failed: RealClick Refresh -> $($_.Exception.Message)" }
Start-Sleep -Milliseconds 1500
try { RealHover $main "btnRefresh" "Refresh tip" | Out-Null } catch { Write-Host "   step failed: RealHover Refresh -> $($_.Exception.Message)" }
Start-Sleep -Milliseconds 1000
try { Tab $main "Customer Master" } catch { Write-Host "   step failed: Tab $main 'Customer Master' -> $($_.Exception.Message)" }
Start-Sleep -Milliseconds 1500
# an MDI workspace (documents are child windows of the MDI client, like a tabbed-MDI ERP): the document in front is the screen,
# a real click on its inner tab is a page selection, a real click on Exit closes it and the next document becomes the screen
$ws = $null
try { Menu $main "View" "Workspace" } catch { Write-Host "   step failed: Menu View Workspace -> $($_.Exception.Message)" }
Start-Sleep -Milliseconds 2500
try { $ws = Top "Sample ERP - Workspace" 20 } catch { Write-Host "   (workspace window not found: $($_.Exception.Message))" }
if ($null -ne $ws) {
    try { [Microsoft.VisualBasic.Interaction]::AppActivate($app.Id) } catch { }
    Start-Sleep -Milliseconds 800
    $soe = $null
    try { $soe = FindByName $ws "Sales Order Entry" "Window" } catch { Write-Host "   (document window not found: $($_.Exception.Message))" }
    if ($null -ne $soe) {
        # the tab pages have a fixed size of 80 px: "Main" is the first, "Notes" the second (WinForms hides tab items from a second UIA client)
        try { RealClickAt $soe "tabsDoc" 120 11 "Notes tab" $ws | Out-Null } catch { Write-Host "   step failed: RealClick Notes tab -> $($_.Exception.Message)" }
        Start-Sleep -Milliseconds 1500
        try { RealClickAt $soe "tabsDoc" 40 11 "Main tab" $ws | Out-Null } catch { Write-Host "   step failed: RealClick Main tab -> $($_.Exception.Message)" }
        Start-Sleep -Milliseconds 1200
        foreach ($q in @("4", "6")) { try { SetVal $soe "txtDocQty" $q; Start-Sleep -Milliseconds 900 } catch { Write-Host "   step failed: SetVal txtDocQty $q -> $($_.Exception.Message)" } }
        try { RealClick $soe "btnDocExit" "Exit (document)" $ws | Out-Null } catch { Write-Host "   step failed: RealClick document Exit -> $($_.Exception.Message)" }
        Start-Sleep -Milliseconds 2500
    }
    try { Act $ws "btnCloseWorkspace" } catch { Write-Host "   step failed: Act btnCloseWorkspace -> $($_.Exception.Message)" }
    Start-Sleep -Milliseconds 1500
}
Front

[LiveTest.Mouse]::SetCursorPos($cursorBefore.X, $cursorBefore.Y) | Out-Null   # the pointer goes back where it was
Write-Host "4. stop the sample app (killed: it is ours), learn, stop the watcher"
Stop-Process -Id $app.Id -Force
Start-Sleep -Milliseconds 5000   # the watcher checks the process every 3 s and saves its status when it sees it exit
$code = Run-Watcher @("--learn")
Write-Host "   learn exit code $code"
Run-Watcher @("--expect") | Out-Null
Stop-Process -Id $watch.Id -Force

$finished = $true
} finally {
    # a failed step must not leave the two programs this test started running
    if (-not $finished) { foreach ($p in @($app, $watch)) { try { Stop-Process -Id $p.Id -Force -ErrorAction Stop } catch { } } }
    try { [LiveTest.Mouse]::SetCursorPos($cursorBefore.X, $cursorBefore.Y) | Out-Null } catch { }
}

Write-Host "5. results"
# the folder is named after the program's friendly name (the sample's product name)
$data = (Get-ChildItem (Join-Path $dataRoot "data") -Directory | Select-Object -First 1).FullName
$events = Get-ChildItem (Join-Path $data "events") -Filter *.jsonl | ForEach-Object { Get-Content $_.FullName } | ForEach-Object { $_ | ConvertFrom-Json }
Write-Host ("   events: {0}" -f $events.Count)
$events | Group-Object act | Sort-Object Count -Descending | ForEach-Object { Write-Host ("     {0,-11} {1}" -f $_.Name, $_.Count) }
Write-Host "   presses by how:"
$events | Where-Object { $_.act -eq "invoke" } | Group-Object how | ForEach-Object { Write-Host ("     {0,-10} {1}" -f ($_.Name, "uia")[[string]::IsNullOrEmpty($_.Name)], $_.Count) }
Write-Host "   presses seen:"
$events | Where-Object { $_.act -eq "invoke" } | ForEach-Object { Write-Host ("     {0}  {1,-12} {2,-9} {3}" -f $_.t.Substring(11, 8), $_.name, ($_.how, "uia")[[string]::IsNullOrEmpty($_.how)], $_.win) }
# the checks a real user's clicks must pass (the toolkit path): a button that raised no event was still counted, and its tip was read
$refreshPresses = @($events | Where-Object { $_.act -eq "invoke" -and $_.name -eq "Refresh" }).Count
$hints = @($events | Where-Object { $_.act -eq "hint" })
$filled = @($events | Where-Object { $_.act -eq "value" -and $_.by -eq "app" })
$tabScreens = @($events | Where-Object { $_.act -eq "select" -and $_.ct -eq "TabItem" }).Count
if ($script:Inconclusive -contains "Refresh") { Write-Host "   PRESS CHECK: inconclusive (the real click was skipped)" }
elseif ($refreshPresses -ge 1) { Write-Host "   PRESS CHECK: ok - the Refresh button (no event of its own) was counted $refreshPresses time(s) by inference" }
else { Write-Host "   PRESS CHECK: MISSING - the Refresh clicks were not counted" }
if ($script:Inconclusive -contains "Refresh tip") { Write-Host "   HINT CHECK: inconclusive (the hover was skipped)" }
elseif ($hints.Count -ge 1) { Write-Host ("   HINT CHECK: ok - {0}" -f (($hints | ForEach-Object { $_.name + ': "' + $_.val + '"' }) -join "; ")) }
else { Write-Host "   HINT CHECK: MISSING - no hover tip was recorded" }
$statusJson = Get-Content (Join-Path $data "collector-status.json") -Raw | ConvertFrom-Json
Write-Host ("   values filled by the program: {0} events ({1} found by re-reading the document, {2} by the field's own event)" -f $filled.Count, $statusJson.ValuesReread, ($statusJson.ValuesFilled - $statusJson.ValuesReread))
# the MDI workspace checks: the document in front was the screen, its inner tab click a selection, its Exit a press, the next document the screen after it
$wsTitle = "Sample ERP - Workspace"
$soeWin = "$wsTitle " + [char]0x203A + " Sales Order Entry"; $pcWin = "$wsTitle " + [char]0x203A + " Price Change"
$soeOpen = @($events | Where-Object { $_.act -eq "open" -and $_.win -eq $soeWin })
$soeClose = @($events | Where-Object { $_.act -eq "close" -and $_.name -eq $soeWin })
$pcAfter = @($events | Where-Object { $_.win -eq $pcWin -and $soeClose.Count -gt 0 -and $_.t -ge $soeClose[0].t })
$innerSel = @($events | Where-Object { $_.act -eq "select" -and $_.ct -eq "TabItem" -and $_.name -eq "Notes" -and $_.win -eq $soeWin })
$docExit = @($events | Where-Object { $_.act -eq "invoke" -and $_.name -eq "Exit" -and $_.win -eq $soeWin })
$docTotals = @($events | Where-Object { $_.act -eq "value" -and $_.by -eq "app" -and $_.name -eq "Total" -and $_.win -eq $soeWin })
if ($null -eq $ws) { Write-Host "   MDI CHECK: inconclusive (the workspace did not open)" }
elseif ($soeOpen.Count -ge 1 -and $soeClose.Count -ge 1 -and $pcAfter.Count -ge 1) { Write-Host ("   MDI CHECK: ok - the document in front was the screen ({0}), its close ended it and {1} became the screen" -f $soeWin, $pcWin) }
else { Write-Host ("   MDI CHECK: MISSING - open {0}, close {1}, next document after it {2}" -f $soeOpen.Count, $soeClose.Count, $pcAfter.Count) }
if ($null -eq $ws) { Write-Host "   INNER TAB CHECK: inconclusive" }
elseif ($script:Inconclusive -contains "Notes tab") { Write-Host "   INNER TAB CHECK: inconclusive (the real click was skipped)" }
elseif ($innerSel.Count -ge 1) { Write-Host ("   INNER TAB CHECK: ok - the Notes page was recorded as selected ({0} event(s), how={1})" -f $innerSel.Count, $innerSel[0].how) }
else { Write-Host "   INNER TAB CHECK: MISSING - the inner tab click left no select event" }
if ($null -eq $ws) { Write-Host "   DOCUMENT EXIT CHECK: inconclusive" }
elseif ($script:Inconclusive -contains "Exit (document)") { Write-Host "   DOCUMENT EXIT CHECK: inconclusive (the real click was skipped)" }
elseif ($docExit.Count -ge 1) { Write-Host ("   DOCUMENT EXIT CHECK: ok - Exit on the document was counted ({0}, {1} total fill(s) seen on it)" -f $docExit[0].how, $docTotals.Count) }
else { Write-Host "   DOCUMENT EXIT CHECK: MISSING - the Exit click that closed the document was not counted" }
Write-Host ("   MDI documents seen: {0}, never recorded: {1}; inner tab selections: {2}; typed values found by re-reading: {3}" -f $statusJson.MdiDocuments, (@($statusJson.DocumentsNeverRecorded) -join ", "), $statusJson.InnerTabSelections, $statusJson.ValuesTypedReread)
Write-Host ("   tab selections: {0}" -f $tabScreens)
Write-Host "   windows seen:"
$events | Group-Object win | Sort-Object Count -Descending | ForEach-Object { Write-Host ("     {0,3}  {1}" -f $_.Count, $_.Name) }
Write-Host "   screens:"
Get-ChildItem (Join-Path $data "screens") -Filter *.md | ForEach-Object { $k = (Select-String -Path $_.FullName -Pattern '^\*\*Kind:\*\* (.*)').Matches[0].Groups[1].Value; Write-Host ("     {0}  ({1})" -f $_.Name, $k) }
Write-Host ("   effects: {0}" -f (Get-ChildItem (Join-Path $data "effects") -Filter *.json).Count)
Write-Host "   collector-status.json:"
Get-Content (Join-Path $data "collector-status.json") | Write-Host
Write-Host "   memory files:"
Get-ChildItem (Join-Path $data "memory") -Filter *.md | ForEach-Object { Write-Host ("     {0} ({1} lines)" -f $_.Name, (Get-Content $_.FullName).Count) }
Write-Host "   first part of health.md:"
Get-Content (Join-Path $data "memory\health.md") -TotalCount 14 | ForEach-Object { Write-Host ("     " + $_) }
Write-Host "   logic.md (what a trainee would know):"
Get-Content (Join-Path $data "memory\logic.md") -Encoding UTF8 | Where-Object { $_ -match '^## |fills|=|opens|Not used|nobody|\?' } | Select-Object -First 45 | ForEach-Object { Write-Host ("     " + $_) }
Write-Host ("   pictures: {0}, OCR text files: {1}" -f (Get-ChildItem (Join-Path $data "screens\shots") -Recurse -Filter *.png -ErrorAction SilentlyContinue).Count, (Get-ChildItem (Join-Path $data "screens\shots") -Recurse -Filter *.png.txt -ErrorAction SilentlyContinue).Count)
Write-Host ("   data folder: {0}" -f $data)
