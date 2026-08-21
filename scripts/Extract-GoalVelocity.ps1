# Extracts per-goal velocity facts from goal-operation journals + git landing commits.
# Emits CSV to stdout. Read-only.
param(
    [string]$JournalDir = ".orchestrator/goal-operations",
    [string]$RepoRoot = "."
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Read-SharedJournalLines([string]$Path) {
    $share = [System.IO.FileShare]::ReadWrite -bor [System.IO.FileShare]::Delete
    $stream = [System.IO.File]::Open($Path, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, $share)
    $reader = New-Object System.IO.StreamReader($stream)
    try {
        while ($null -ne ($line = $reader.ReadLine())) {
            Write-Output $line
        }
    }
    finally {
        $reader.Dispose()
        $stream.Dispose()
    }
}

# goalPrefix -> landing time (UTC)
$landings = @{}
$log = & git -C $RepoRoot log --grep="Integrate goal/" --format="%aI|%s" main
foreach ($line in $log) {
    $parts = $line -split '\|', 2
    if ($parts.Count -lt 2) { continue }
    if ($parts[1] -match 'Integrate goal/([0-9a-f]+)') {
        $prefix = $matches[1]
        if (-not $landings.ContainsKey($prefix)) {
            $landings[$prefix] = ([datetimeoffset]$parts[0]).ToUniversalTime()
        }
    }
}

$out = New-Object System.Collections.Generic.List[object]

foreach ($file in Get-ChildItem -Path $JournalDir -Filter *.jsonl) {
    $goalId = $file.BaseName
    $prefix = $goalId.Substring(0, [Math]::Min(8, $goalId.Length))

    $first = $null
    $last = $null
    $dispatches = 0
    $evStarts = New-Object System.Collections.Generic.List[datetimeoffset]
    $evEnds = New-Object System.Collections.Generic.List[datetimeoffset]
    $acceptRun = 0
    $escalations = 0
    $buildMs = 0

    foreach ($line in (Read-SharedJournalLines $file.FullName)) {
        $t = $null
        if ($line -match '"at":"([^"]+)"') {
            $t = ([datetimeoffset]$matches[1]).ToUniversalTime()
            if ($null -eq $first) { $first = $t }
            $last = $t
        }
        if ($line -match 'Dispatched \d+ tasks') { $dispatches++ }
        if ($line -match 'Running focused reviewer evidence') { if ($t) { $evStarts.Add($t) } }
        if ($line -match 'focused evidence (passed|failed)') { if ($t) { $evEnds.Add($t) } }
        if ($line -match '"operation":"conductor:acceptance"') { $acceptRun++ }
        if ($line -match 'escalat') { $escalations++ }
        if ($line -match '"buildPhaseMilliseconds":(\d+)') { $buildMs += [int]$matches[1] }
    }

    if ($null -eq $first) { continue }

    $evSeconds = 0.0
    $pairs = [Math]::Min($evStarts.Count, $evEnds.Count)
    for ($i = 0; $i -lt $pairs; $i++) {
        $d = ($evEnds[$i] - $evStarts[$i]).TotalSeconds
        if ($d -ge 0 -and $d -lt 14400) { $evSeconds += $d }
    }

    $landed = $null
    $wallMin = $null
    if ($landings.ContainsKey($prefix)) {
        $landed = $landings[$prefix]
        $wallMin = [Math]::Round((($landed - $first).TotalMinutes), 1)
    }

    $out.Add([pscustomobject]@{
        goal          = $prefix
        firstOpUtc    = $first.ToString("yyyy-MM-ddTHH:mm:ssZ")
        landedUtc     = if ($landed) { $landed.ToString("yyyy-MM-ddTHH:mm:ssZ") } else { "" }
        wallMinutes   = if ($null -ne $wallMin) { $wallMin } else { "" }
        dispatches    = $dispatches
        evidenceRuns  = $evStarts.Count
        evidenceMin   = [Math]::Round($evSeconds / 60.0, 1)
        acceptanceOps = $acceptRun
        escalationOps = $escalations
        buildPhaseMin = [Math]::Round($buildMs / 60000.0, 1)
        journalLines  = (Read-SharedJournalLines $file.FullName | Measure-Object -Line).Lines
    })
}

$out | Sort-Object firstOpUtc | ConvertTo-Csv -NoTypeInformation
