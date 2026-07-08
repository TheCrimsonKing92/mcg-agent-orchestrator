# Read-only per-task summary for a goal: status line, last failure event, newest blockers field.
# Usage: ./scripts/Get-GoalTaskSummary.ps1 <goalPrefix>
param(
    [Parameter(Mandatory = $true)][string]$GoalPrefix
)

$repoRoot = Split-Path $PSScriptRoot -Parent
$logs = Join-Path $repoRoot '.orchestrator\logs'

for ($n = 1; $n -le 5; $n++) {
    $out = & (Join-Path $repoRoot 'mcg-orchestrator.cmd') task $GoalPrefix $n 2>$null
    if (-not $out) { continue }
    $head = ($out | Select-Object -First 1)
    $fail = ($out | Where-Object { $_ -match 'TaskFailed' } | Select-Object -Last 1)
    "TASK $n : $head"
    if ($fail) { "  LASTFAIL: $($fail.Trim())" }
    $taskId = ($head -split ' ')[0]
    $newestLog = Get-ChildItem -Path $logs -Filter "$GoalPrefix-$taskId-*.out.log" -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if ($newestLog) {
        $b = Select-String -Path $newestLog.FullName -Pattern '^blockers: (.*)$' -ErrorAction SilentlyContinue |
            Select-Object -Last 1
        if ($b) { "  BLOCKERS: $($b.Matches[0].Groups[1].Value)" }
    }
}
