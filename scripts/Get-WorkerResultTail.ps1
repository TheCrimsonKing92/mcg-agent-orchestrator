# Read-only tail of the newest worker out.log for a goal (optionally scoped to a task prefix).
# Usage: ./scripts/Get-WorkerResultTail.ps1 <goalPrefix> [taskPrefix] [-Chars 800]
param(
    [Parameter(Mandatory = $true)][string]$GoalPrefix,
    [string]$TaskPrefix = '*',
    [int]$Chars = 800
)

$logs = Join-Path (Split-Path $PSScriptRoot -Parent) '.orchestrator\logs'
$newest = Get-ChildItem -Path $logs -Filter "$GoalPrefix-$TaskPrefix-*.out.log" -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $newest) { "no out.log for $GoalPrefix-$TaskPrefix"; exit 0 }
"FILE: $($newest.Name)"
$text = Get-Content $newest.FullName -Raw
if ($text.Length -gt $Chars) { $text = $text.Substring($text.Length - $Chars) }
$text
