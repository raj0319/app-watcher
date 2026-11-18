# Helper for live-test.ps1: invokes one menu item or button of the sample app from a separate process.
# A UI Automation Invoke does not return while the window it opens is modal, so the main test script
# starts this detached and carries on. Only the given process is touched.
param([int]$ProcessId, [string]$Name, [string]$AutomationId = "")
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$pidCond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $ProcessId)
$item = $null
foreach ($i in 1..30) {
    foreach ($r in [System.Windows.Automation.AutomationElement]::RootElement.FindAll([System.Windows.Automation.TreeScope]::Children, $pidCond)) {
        foreach ($d in $r.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)) {
            if (($AutomationId -ne "" -and $d.Current.AutomationId -eq $AutomationId) -or ($AutomationId -eq "" -and $d.Current.Name -eq $Name -and $d.Current.ControlType.ProgrammaticName -eq "ControlType.MenuItem")) { $item = $d; break }
        }
        if ($null -ne $item) { break }
    }
    if ($null -ne $item) { break }
    Start-Sleep -Milliseconds 200
}
if ($null -eq $item) { exit 2 }
try { $item.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() } catch { }
exit 0
