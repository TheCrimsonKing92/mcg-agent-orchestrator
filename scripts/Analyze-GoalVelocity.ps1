# Era-segmented analysis of the goal-velocity dataset. Read-only.
param([string]$Csv)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$rows = Import-Csv $Csv
foreach ($r in $rows) {
    $r | Add-Member -NotePropertyName Started -NotePropertyValue ([datetime]$r.firstOpUtc) -Force
    $r | Add-Member -NotePropertyName Landed -NotePropertyValue ([bool]$r.landedUtc) -Force
    $r | Add-Member -NotePropertyName Wall -NotePropertyValue $(if ($r.wallMinutes) { [double]$r.wallMinutes } else { $null }) -Force
    $r | Add-Member -NotePropertyName Disp -NotePropertyValue ([int]$r.dispatches) -Force
    $r | Add-Member -NotePropertyName EvMin -NotePropertyValue ([double]$r.evidenceMin) -Force
    $r | Add-Member -NotePropertyName EvRuns -NotePropertyValue ([int]$r.evidenceRuns) -Force
}

function Median($values) {
    $v = @($values | Where-Object { $null -ne $_ } | Sort-Object)
    if ($v.Count -eq 0) { return $null }
    if ($v.Count % 2 -eq 1) { return $v[[int](($v.Count - 1) / 2)] }
    return [Math]::Round((($v[$v.Count / 2 - 1] + $v[$v.Count / 2]) / 2), 1)
}

Write-Output "=== WEEKLY ERAS (goal start week) ==="
Write-Output "week_start | started | landed | land% | medWall | medDisp | medEvMin | evRuns>0"
$groups = $rows | Group-Object { $_.Started.Date.AddDays( - [int]$_.Started.DayOfWeek ).ToString("yyyy-MM-dd") }
foreach ($g in ($groups | Sort-Object Name)) {
    $landedRows = @($g.Group | Where-Object { $_.Landed -and $null -ne $_.Wall -and $_.Wall -gt 0 })
    $lp = if ($g.Count -gt 0) { [Math]::Round(100.0 * $landedRows.Count / $g.Count, 0) } else { 0 }
    $mw = Median ($landedRows | ForEach-Object { $_.Wall })
    $md = Median ($landedRows | ForEach-Object { $_.Disp })
    $me = Median ($landedRows | ForEach-Object { $_.EvMin })
    $ev = @($g.Group | Where-Object { $_.EvRuns -gt 0 }).Count
    Write-Output ("{0} | {1,7} | {2,6} | {3,4}% | {4,7} | {5,7} | {6,8} | {7}" -f $g.Name, $g.Count, $landedRows.Count, $lp, $mw, $md, $me, $ev)
}

Write-Output ""
Write-Output "=== EVIDENCE-RUN COST DISTRIBUTION (landed goals with >0 runs) ==="
$ev = @($rows | Where-Object { $_.Landed -and $_.EvRuns -gt 0 })
Write-Output ("goals with evidence runs: {0}" -f $ev.Count)
if ($ev.Count -gt 0) {
    $buckets = @{ "<1min" = 0; "1-5min" = 0; "5-20min" = 0; "20-60min" = 0; ">60min" = 0 }
    foreach ($r in $ev) {
        if ($r.EvMin -lt 1) { $buckets["<1min"]++ }
        elseif ($r.EvMin -lt 5) { $buckets["1-5min"]++ }
        elseif ($r.EvMin -lt 20) { $buckets["5-20min"]++ }
        elseif ($r.EvMin -lt 60) { $buckets["20-60min"]++ }
        else { $buckets[">60min"]++ }
    }
    foreach ($k in @("<1min", "1-5min", "5-20min", "20-60min", ">60min")) {
        Write-Output ("  {0,-9} {1}" -f $k, $buckets[$k])
    }
    $tot = ($ev | Measure-Object -Property EvMin -Sum).Sum
    Write-Output ("  total evidence minutes across landed goals: {0}" -f [Math]::Round($tot, 0))
}

Write-Output ""
Write-Output "=== DISPATCH-COUNT vs WALL, by era ==="
$eras = @(
    @{ n = "Jun14-Jun30"; s = [datetime]"2026-06-14"; e = [datetime]"2026-07-01" },
    @{ n = "Jul01-Jul15"; s = [datetime]"2026-07-01"; e = [datetime]"2026-07-16" },
    @{ n = "Jul16-Jul31"; s = [datetime]"2026-07-16"; e = [datetime]"2026-08-01" },
    @{ n = "Aug01-Aug04"; s = [datetime]"2026-08-01"; e = [datetime]"2026-08-05" }
)
foreach ($era in $eras) {
    $sub = @($rows | Where-Object { $_.Landed -and $null -ne $_.Wall -and $_.Wall -gt 0 -and $_.Started -ge $era.s -and $_.Started -lt $era.e })
    if ($sub.Count -lt 3) { Write-Output ("{0}: n={1} too few" -f $era.n, $sub.Count); continue }
    $mw = Median ($sub | ForEach-Object { $_.Wall })
    $md = Median ($sub | ForEach-Object { $_.Disp })
    $perRound = Median ($sub | Where-Object { $_.Disp -gt 0 } | ForEach-Object { [Math]::Round($_.Wall / $_.Disp, 1) })
    # Pearson r between dispatches and wall
    $n = $sub.Count
    $mx = ($sub | Measure-Object -Property Disp -Average).Average
    $my = ($sub | Measure-Object -Property Wall -Average).Average
    $num = 0.0; $dx = 0.0; $dy = 0.0
    foreach ($r in $sub) { $a = $r.Disp - $mx; $b = $r.Wall - $my; $num += $a * $b; $dx += $a * $a; $dy += $b * $b }
    $rr = if ($dx -gt 0 -and $dy -gt 0) { [Math]::Round($num / [Math]::Sqrt($dx * $dy), 2) } else { "n/a" }
    Write-Output ("{0}: n={1,3}  medWall={2,6}  medDisp={3,4}  medMinPerRound={4,5}  r(disp,wall)={5}" -f $era.n, $n, $mw, $md, $perRound, $rr)
}
