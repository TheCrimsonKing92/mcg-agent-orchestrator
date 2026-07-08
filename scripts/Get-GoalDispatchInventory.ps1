# Read-only inventory of a goal's dispatch artifacts: one line per generation with exit code.
# Usage: ./scripts/Get-GoalDispatchInventory.ps1 <goalPrefix>
param(
    [Parameter(Mandatory = $true)][string]$GoalPrefix
)

$logs = Join-Path $PSScriptRoot '..\.orchestrator\logs'
$found = $false
Get-ChildItem -Path $logs -Filter "$GoalPrefix-*.dispatch.json" -ErrorAction SilentlyContinue |
    Sort-Object Name |
    ForEach-Object {
        $found = $true
        $base = $_.BaseName -replace '\.dispatch$', ''
        $exitPath = Join-Path $logs "$base.exit.txt"
        $exit = if (Test-Path $exitPath) { (Get-Content $exitPath -Raw).Trim() } else { 'running' }
        $hbPath = Join-Path $logs "$base.heartbeat.json"
        $state = ''
        if (Test-Path $hbPath) {
            $m = [regex]::Match((Get-Content $hbPath -Raw), '"state"\s*:\s*"([a-z-]+)"')
            if ($m.Success) { $state = $m.Groups[1].Value }
        }
        "GEN $base exit=$exit hb=$state"
    }
if (-not $found) { "no dispatch artifacts for prefix $GoalPrefix" }
