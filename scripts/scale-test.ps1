# Scale test: a synthetic 5-user office, 30 working days (~330k-600k events), learned from day one, incrementally,
# merged from several PCs and rotated by retention. Prints the numbers (events, seconds, peak MB, memory file sizes)
# and fails when the learner, the merge or the retention gives a different memory than they should.
# Everything is synthetic and lives in the temp folder; nothing of this PC is read. Takes a few minutes.
#   scripts\scale-test.ps1                 the big run
#   scripts\scale-test.ps1 -Report out.txt keep the numbers in a file as well
param([string]$Report = (Join-Path ([IO.Path]::GetTempPath()) 'appwatcher-scale-report.txt'))

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$env:APPWATCHER_SCALE = '1'
$env:APPWATCHER_SCALE_REPORT = $Report
if (Test-Path $Report) { Remove-Item $Report -Force }

Push-Location $root
try {
    & dotnet test (Join-Path $root 'tests\AppWatcher.Tests') --filter 'FullyQualifiedName~ScaleTests.Big_office' --logger 'console;verbosity=minimal' | Out-Host
    $code = $LASTEXITCODE
} finally { Pop-Location }

if (Test-Path $Report) {
    Write-Host ''
    Write-Host '=== Scale numbers ===' -ForegroundColor Cyan
    Get-Content $Report | Write-Host
}
exit $code
