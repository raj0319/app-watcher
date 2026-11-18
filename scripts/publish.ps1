<#
.SYNOPSIS
  Builds the single self-contained AppWatcher.exe into .\publish (needs the .NET 8 SDK).

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File scripts\publish.ps1
#>
$ErrorActionPreference = "Stop"
$root = Resolve-Path (Join-Path $PSScriptRoot "..")
$out = Join-Path $root "publish"
dotnet publish (Join-Path $root "src\AppWatcher\AppWatcher.csproj") -c Release -r win-x64 --self-contained true -o $out
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
Get-ChildItem $out -Filter *.exe | ForEach-Object { Write-Host ("Built {0} ({1:N1} MB)" -f $_.FullName, ($_.Length / 1MB)) }
