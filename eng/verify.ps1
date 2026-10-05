[CmdletBinding()]
param([switch]$RequireNative)
$ErrorActionPreference = "Stop"
& pwsh -NoProfile -File (Join-Path $PSScriptRoot 'restore-adapter-sdk.ps1')
if ($LASTEXITCODE -ne 0) { throw 'Fixed SDK verification failed.' }
$projects = @("src/MirrorPulse.Adapter.Smb.Worker/MirrorPulse.Adapter.Smb.Worker.csproj", "tests/MirrorPulse.Adapter.Smb.Worker.Tests/MirrorPulse.Adapter.Smb.Worker.Tests.csproj", "tools/MirrorPulse.Adapter.Smb.Conformance/MirrorPulse.Adapter.Smb.Conformance.csproj")
foreach ($project in $projects) {
    & dotnet restore $project --locked-mode
    if ($LASTEXITCODE -ne 0) { throw "Restore failed for $project." }
    & dotnet build $project --configuration Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw "Build failed for $project." }
    & dotnet format $project --no-restore --verify-no-changes
    if ($LASTEXITCODE -ne 0) { throw "Formatting failed for $project." }
}
$filter = if ($RequireNative) { 'FullyQualifiedName~SmbWorker' } else { 'TestCategory!=SmbNative' }
& dotnet test $projects[1] -c Release --no-build --no-restore --filter $filter --logger 'trx;LogFileName=smb-worker.trx' --results-directory artifacts/test-results
if ($LASTEXITCODE -ne 0) { throw 'SMB conformance failed.' }
[xml]$trx = Get-Content -LiteralPath artifacts/test-results/smb-worker.trx -Raw
$counts = $trx.TestRun.ResultSummary.Counters
$expected = if ($RequireNative) { 29 } else { 12 }
if ($counts.total -ne $expected -or $counts.executed -ne $expected -or $counts.passed -ne $expected -or $counts.notExecuted -ne 0) {
    throw 'Every selected SMB boundary must execute without skips.'
}
