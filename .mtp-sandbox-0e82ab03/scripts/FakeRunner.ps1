param([Parameter(ValueFromRemainingArguments = $true)][string[]]$Arguments)
if ($Arguments.Count -gt 0 -and $Arguments[0] -eq 'msbuild') {
    $targetPath = 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\26066a2c\.mtp-sandbox-0e82ab03\tests\Mcg.AgentOrchestrator.Infrastructure.Tests\bin\Debug\net10.0\Mcg.AgentOrchestrator.Infrastructure.Tests.dll'
    $sourcePath = 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\26066a2c\.mtp-sandbox-0e82ab03\bin\Mcg.AgentOrchestrator.Infrastructure.Tests\Debug\receipt-source.cs'
    $open = [char]123
    $close = [char]125
    Write-Output ($open + '"Properties":' + $open + '"TargetPath":' + ($targetPath | ConvertTo-Json -Compress) + ',"TargetFramework":"net10.0","RuntimeIdentifier":""' + $close + ',"Items":' + $open + '"Compile":[' + $open + '"Identity":' + ($sourcePath | ConvertTo-Json -Compress) + $close + ']' + $close + $close)
    exit 0
}
$Arguments | Set-Content -LiteralPath 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\26066a2c\.mtp-sandbox-0e82ab03\runner-arguments.txt'
Write-Output 'stub stdout'
Write-Output "stub temp=$env:TEMP tmp=$env:TMP tmpdir=$env:TMPDIR"
[Console]::Error.WriteLine('stub stderr')
if ('success' -eq 'hang') {
    $lock = [System.IO.File]::Open('C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\26066a2c\.mtp-sandbox-0e82ab03\hang.lock', 'OpenOrCreate', 'ReadWrite', 'None')
    try {
        Set-Content -LiteralPath 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\26066a2c\.mtp-sandbox-0e82ab03\hang.ready' -Value 'ready'
        Write-Output 'hang descendant ready'
        $never = [System.Threading.ManualResetEvent]::new($false)
        [void]$never.WaitOne()
    }
    finally {
        $lock.Dispose()
    }
}
if ('success' -eq 'root-exits-descendant-locks') {
    $childCommand = @'
$lock = [System.IO.File]::Open('C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\26066a2c\.mtp-sandbox-0e82ab03\hang.lock', 'OpenOrCreate', 'ReadWrite', 'None')
try {
    Set-Content -LiteralPath 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\26066a2c\.mtp-sandbox-0e82ab03\hang.ready' -Value 'ready'
    $never = [System.Threading.ManualResetEvent]::new($false)
    [void]$never.WaitOne()
}
finally {
    $lock.Dispose()
}
'@
    $childEncoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($childCommand))
    Start-Process powershell.exe -ArgumentList @('-NoProfile', '-EncodedCommand', $childEncoded) -NoNewWindow | Out-Null
    while (-not (Test-Path -LiteralPath 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\26066a2c\.mtp-sandbox-0e82ab03\hang.ready')) {
        [System.Threading.Thread]::Sleep(25)
    }
    Write-Output 'hang descendant ready'
    while (-not (Test-Path -LiteralPath 'C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\26066a2c\.mtp-sandbox-0e82ab03\root.release')) {
        [System.Threading.Thread]::Sleep(25)
    }
    Write-Output 'root released after wrapper timeout'
    exit 0
}
if ('success' -eq 'late-output') {
    $lateCommand = "Start-Sleep -Seconds 6; Write-Output 'late descendant output'"
    $lateEncoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($lateCommand))
    Start-Process powershell.exe -ArgumentList @('-NoProfile', '-EncodedCommand', $lateEncoded) -NoNewWindow | Out-Null
}
$resultsIndex = [Array]::IndexOf($Arguments, '--results-directory')
$fileIndex = [Array]::IndexOf($Arguments, '--report-trx-filename')
if ('success' -ne 'no-trx') {
    $trxPath = Join-Path $Arguments[$resultsIndex + 1] $Arguments[$fileIndex + 1]
    '<?xml version="1.0" encoding="utf-8"?>
<TestRun>
  <ResultSummary outcome="Completed">
    <Counters total="1" executed="1" passed="1" failed="0" notExecuted="0" />
  </ResultSummary>
  <Results></Results>
</TestRun>' | Set-Content -LiteralPath $trxPath -Encoding UTF8
}
exit 0