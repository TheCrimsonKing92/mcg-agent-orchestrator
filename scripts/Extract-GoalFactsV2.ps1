# Per-goal typed-field aggregates from goal-operation journals. Mechanical counts only. Read-only.
param(
    [string]$JournalDir = ".orchestrator/goal-operations",
    [string]$RepoRoot = "."
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$landings = @{}
foreach ($line in (& git -C $RepoRoot log --grep="Integrate goal/" --format="%aI|%s" main)) {
    $parts = $line -split '\|', 2
    if ($parts.Count -lt 2) { continue }
    if ($parts[1] -match 'Integrate goal/([0-9a-f]+)') {
        $p = $matches[1]
        if (-not $landings.ContainsKey($p)) { $landings[$p] = ([datetimeoffset]$parts[0]).ToUniversalTime() }
    }
}

$out = New-Object System.Collections.Generic.List[object]

foreach ($file in Get-ChildItem -Path $JournalDir -Filter *.jsonl) {
    $goalId = $file.BaseName
    $prefix = $goalId.Substring(0, [Math]::Min(8, $goalId.Length))

    $first = $null
    $last = $null
    $firstDispatch = $null
    $dispatches = 0
    $accPassed = 0
    $accFailed = 0
    $accLock = 0
    $accSlot = 0
    $accOther = 0
    $partGreen = 0
    $partRed = 0
    $partReuse = 0
    $partForced = 0
    $regate = 0
    $failedChecks = New-Object System.Collections.Generic.HashSet[string]

    $stream = [System.IO.File]::Open($file.FullName, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite)
    $reader = New-Object System.IO.StreamReader($stream)
    try {
        while ($null -ne ($line = $reader.ReadLine())) {
            if ($line -match '"at":"([^"]+)"') {
                $t = ([datetimeoffset]$matches[1]).ToUniversalTime()
                if ($null -eq $first) { $first = $t }
                $last = $t
                if ($line -match 'Dispatched \d+ tasks') {
                    $dispatches++
                    if ($null -eq $firstDispatch) { $firstDispatch = $t }
                }
            }
            if ($line -match '"acceptanceOutcome":"([^"]+)"') {
                switch -Regex ($matches[1]) {
                    '^passed|^gate-passed' { $accPassed++ }
                    '^failed' { $accFailed++ }
                    'build-lock|BUILD_LOCK' { $accLock++ }
                    'build-slot' { $accSlot++ }
                    default { $accOther++ }
                }
            }
            if ($line -match '"partitionVerdict":"GREEN"') { $partGreen++ }
            if ($line -match '"partitionVerdict":"RED"') { $partRed++ }
            if ($line -match '"partitionReuseAttemptCount":([1-9]\d*)') { $partReuse++ }
            if ($line -match '"partitionForcedFullRerun":true') { $partForced++ }
            if ($line -match '"operatorRegateCount":([1-9]\d*)') { $regate++ }
            if ($line -match '"failedCheckNames":\[([^\]]+)\]') {
                foreach ($m in [regex]::Matches($matches[1], '"([^"]+)"')) {
                    [void]$failedChecks.Add($m.Groups[1].Value)
                }
            }
        }
    }
    finally {
        $reader.Dispose()
        $stream.Dispose()
    }

    if ($null -eq $first) { continue }

    $landed = if ($landings.ContainsKey($prefix)) { $landings[$prefix] } else { $null }

    $out.Add([pscustomobject]@{
        goal             = $prefix
        firstOpUtc       = $first.ToString("yyyy-MM-ddTHH:mm:ssZ")
        firstDispatchUtc = if ($firstDispatch) { $firstDispatch.ToString("yyyy-MM-ddTHH:mm:ssZ") } else { "" }
        lastOpUtc        = $last.ToString("yyyy-MM-ddTHH:mm:ssZ")
        landedUtc        = if ($landed) { $landed.ToString("yyyy-MM-ddTHH:mm:ssZ") } else { "" }
        wallMinutes      = if ($landed) { [Math]::Round((($landed - $first).TotalMinutes), 1) } else { "" }
        activeMinutes    = if ($landed -and $firstDispatch) { [Math]::Round((($landed - $firstDispatch).TotalMinutes), 1) } else { "" }
        dispatches       = $dispatches
        accPassed        = $accPassed
        accFailed        = $accFailed
        accBlockedLock   = $accLock
        accBlockedSlot   = $accSlot
        accOther         = $accOther
        partGreen        = $partGreen
        partRed          = $partRed
        partReuseUsed    = $partReuse
        partForcedRerun  = $partForced
        operatorRegates  = $regate
        failedCheckKinds = ($failedChecks -join ";")
    })
}

$out | Sort-Object firstOpUtc | ConvertTo-Csv -NoTypeInformation
