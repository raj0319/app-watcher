<#
.SYNOPSIS
  Checks that no string from a list kept OUTSIDE the repository (real names, ids, addresses of a client) appears
  anywhere under tests\. Run before committing fixtures copied from a real session.
.EXAMPLE
  powershell -ExecutionPolicy Bypass -File scripts\check-fixtures.ps1 -Forbidden D:\private\forbidden-strings.txt
#>
param([Parameter(Mandatory = $true)][string]$Forbidden, [string]$Folder = "")
$ErrorActionPreference = "Stop"
if ($Folder -eq "") { $Folder = Join-Path (Split-Path $PSScriptRoot) "tests" }
$words = Get-Content $Forbidden | Where-Object { $_.Trim().Length -gt 0 }
$hits = 0
foreach ($f in Get-ChildItem $Folder -Recurse -File | Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' }) {
    $text = (Get-Content $f.FullName -Raw -ErrorAction SilentlyContinue)
    if ($null -eq $text) { continue }
    foreach ($w in $words) {
        $word = $w.Trim().ToLowerInvariant()
        # short words must stand alone ("nate" is also the tail of "Indeterminate"); longer ones are found anywhere
        $pattern = if ($word.Length -lt 6) { "(?<![a-z0-9])" + [regex]::Escape($word) + "(?![a-z0-9])" } else { [regex]::Escape($word) }
        if ([regex]::IsMatch($text.ToLowerInvariant(), $pattern)) { Write-Host ("FOUND '{0}' in {1}" -f $word, $f.FullName); $hits++ }
    }
}
if ($hits -gt 0) { Write-Host "$hits hit(s): do not commit."; exit 1 }
Write-Host "clean: none of the $($words.Count) strings appears under $Folder"
exit 0
