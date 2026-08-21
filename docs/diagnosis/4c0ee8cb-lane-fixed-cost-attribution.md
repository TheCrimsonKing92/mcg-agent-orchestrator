# Infrastructure lane fixed-cost attribution

## Attribution

The named removable per-process mechanism is the synchronous orphan-root reap in
`AssemblyTempRedirect.Install`: every Infrastructure test host traverses the accumulated real
`%LOCALAPPDATA%\Temp\Low\mcg-tests` population before discovery and previously attempted every
dead-PID root and every age-expired root as separate sets. The `icacls` setter timeout is excluded
as the mechanism for the fastest lane.

This attribution is based on measured controls, not the coincidence between 119.1 seconds and the
two-minute setter bound. The pre-existing source receipt measured test-host startup at 53.5 seconds
with 1,106 accumulated roots and 3.1 seconds after those roots were cleared: a 50.4-second delta at
the reap seam. At the first 2026-08-21 census, the live root had 1,293 PID-shaped roots, 1,284 with
no live PID, 1,213 older than twelve hours, and 1,211 selected by both rules. A read-only recursive
walk of all 1,293 real roots took 41.6 seconds; the same walk with file metadata inspection did not
finish inside 120 seconds. No individual root took one second or contained 10,000 entries, so the
cost is aggregate fanout across the shared population rather than one pathological tree.

A second read-only census at 2026-08-21T16:10:05.4840476Z found 1,683 siblings: 1,303 PID roots,
1,296 dead-PID roots, 1,217 older than twelve hours, and 1,216 selected by both rules. Enumeration,
process snapshot, orphan selection, age selection, and overlap calculation together took 316 ms.
The population grew while this goal was active; all timing receipts therefore need their own count
and timestamp.

The negative control was a synthetic population with the same 1,284-root count, one nested fixture
and one file per root. Deleting all of it took 714 ms; deleting 32 took 59 ms. Root count by itself
therefore does not explain the floor. The expensive input is the accumulated content of the real
shared population, traversed synchronously and, for 1,211 roots, eligible through both rules.

The closing controlled observation removed only orphan roots older than twelve hours from the real
Low test-temp population, reducing it from 1,680 roots to 82 (95 percent). Four same-named lanes on
uncontended gates then lost 124.8 to 129.2 seconds each despite having very different test workloads.
`Remainder balance A` fell from 138.5 seconds to 13.7 seconds, breaking the 119.1-second historical
floor. The nearly constant absolute reduction, coupled with the synthetic same-count control above,
attributes the fixed cost to synchronous traversal/reaping of the accumulated real orphan content,
not to lane work, root count alone, or the `icacls` setter.

## Timing breakdown

| Phase or control | Elapsed | Outcome |
| --- | ---: | --- |
| Fastest supplied `shard-complete` receipt | 119,100 ms | Lane total; raw receipt is not present in this worktree |
| Declared work for the matching small lane | 8,600 ms | Supplied estimate |
| Historical host startup with 1,106 accumulated roots | 53,500 ms | Existing in-source receipt |
| Historical host startup after clearing those roots | 3,100 ms | Existing in-source negative control |
| Historical startup delta at the reap seam | 50,400 ms | 53,500 - 3,100 |
| `icacls` query of the live Low root | 15 ms | Completed; valid inheritable Low label |
| Live sibling census and dead/old selection | 213 ms | 1,293 owned; 1,284 dead; 1,213 old; 1,211 overlap |
| Timestamped follow-up census and selection | 316 ms | 1,303 owned; 1,296 dead; 1,217 old; 1,216 overlap |
| One read-only recursive walk of all live owned roots | 41,600 ms | Completed; no root exceeded 1,000 ms |
| Recursive walk plus per-file metadata | >120,000 ms | Command deadline fired before completion |
| Delete representative synthetic 1,284-root population | 714 ms | Completed |
| Delete representative synthetic bounded set | 59 ms | 32 completed |
| Real Low-root population before operator control | 1,680 roots | Before uncontended gate |
| Real Low-root population after operator control | 82 roots | Only roots older than twelve hours removed |
| `Remainder balance A` before / after | 138,500 / 13,700 ms | 124,800 ms removed |
| `Dotnet build slots` before / after | 335,400 / 208,300 ms | 127,100 ms removed |
| `Worker dispatch fixtures` before / after | 354,800 / 225,600 ms | 129,200 ms removed |
| `Goal worktree cleanup` before / after | 425,500 / 299,500 ms | 126,000 ms removed |
| Independent focused evidence before / after | 214,400 / 91,500 ms | 122,900 ms removed; 206 / 207 tests |

The matched gate comparison closes the previously unattributed fixed-floor budget. The before gate
ran from 2026-08-21T04:04:53Z to 2026-08-21T04:26:34Z. The after gate started at
2026-08-21T16:28:36Z as attempt
`19d16954-0-20260821162836436-b849b932ae65404b8fda688149252d83`; it was verified to be the only gate
publishing progress events at the time. For the smallest matched lane:

`removed_fixed_cost = 138.5s before - 13.7s after = 124.8s`.

The other three same-named lanes lost 127.1, 129.2, and 126.0 seconds. Their mean removed cost is
126.8 seconds. Because the removed amount is approximately constant rather than proportional to
lane duration, it is a per-process cost. Because reducing only the orphan population made that
constant disappear and produced a 13.7-second lane where none of 71 prior receipts was below 119.1
seconds, the historical fixed floor has no remaining unattributed portion. The earlier
`fixed_floor_budget = 119.1s - 8.6s = 110.5s` was an estimate assembled from unmatched observations;
the controlled same-lane delta supersedes it.

The gates had different overall topology: the before run had 16 lanes and the after run had 18 after
goal `ea1a518e` split one collection. The four comparisons remain valid because they use the same
named lanes, but a whole-gate wall-clock delta is not computed. Roughly 126.8 seconds multiplied by
the lane count is an inference, not an observed gate-level saving. The independent focused-evidence
control corroborates the result: goal `58ae2e59` fell from 214.4 seconds for 206 tests before cleanup
to 91.5 seconds for 207 tests afterward, a 122.9-second absolute reduction.

## Why the setter hypothesis is excluded

1. A fired `SetIntegrity` deadline costs at least 120.0 seconds inside module initialization, but
   the fastest supplied lane is 119.1 seconds including discovery and test work.
2. The two normal LOCALAPPDATA candidate sources resolve to the same path. A failed first candidate
   would repeat query/set/query on the second, predicting approximately 240 seconds.
3. The live root has a parser-compatible inheritable Low label and its read-only query completed in
   15 ms. The known historical 120-second failure used recursive `/T` labeling with undrained pipes;
   the current setter is non-recursive and drains both streams.

The production `IcaclsIntegrityLabeler` remains unchanged. Elapsed label-set diagnostics are owned
by the test-host initializer; its existing Boolean contract is sufficient here because the setter
did not run in the observed valid-Low path. A test-only diagnostic seam supplies the simulated
timeout outcome used by the fallback negative control.

## Remedy and criterion 2 handoff

The candidate mitigation unions the dead-PID and age-expired sets case-insensitively, sorts them
oldest first, and attempts at most 32 roots per process. Both abandonment rules remain active,
process-exit cleanup remains active, and failed deletes remain best effort. This bounds deletion
attempts without weakening Low-integrity confinement or adding a shared lock.

The synthetic control predicts a bounded-set reduction from 714 ms to 59 ms for a shallow
1,284-root population. Criterion 2 is satisfied by the operator-owned matched receipts above: the
same named small lane measurably completed below 119 seconds after the removable population cost was
cleared. This is not proof of a durable fix. The population grew from 1,293 to 1,680 in roughly three
hours of normal board activity, and the matched after run followed an operator cleanup rather than a
regrown-population trial of the bounded reap. A future uncontended gate with a regrown population is
required before crediting the candidate mitigation with preventing recurrence; until then, manual
cleanup is a recurring operational workaround, not the solution.

## Measurement commands

Commands ran from the goal worktree. All live-root commands were read-only. The only deletion was a
GUID-named synthetic directory validated beneath the worker's own `%TEMP%` and removed afterward.

```powershell
$lowRoot = Join-Path $env:LOCALAPPDATA 'Temp\Low\mcg-tests'
$dirs = @(Get-ChildItem -LiteralPath $lowRoot -Directory)
$live = @{}; Get-Process | ForEach-Object { $live[$_.Id] = $true }
$owned = @($dirs | Where-Object Name -Match '^p[0-9a-fA-F]+$')
$dead = @($owned | Where-Object { $ownerId = [Convert]::ToInt32($_.Name.Substring(1), 16); -not $live.ContainsKey($ownerId) })
$old = @($owned | Where-Object LastWriteTimeUtc -LT ([DateTime]::UtcNow.AddHours(-12)))
```

```powershell
$timer = [Diagnostics.Stopwatch]::StartNew()
foreach ($candidate in [IO.Directory]::GetDirectories($lowRoot)) {
  foreach ($entry in [IO.Directory]::EnumerateFileSystemEntries($candidate, '*', [IO.SearchOption]::AllDirectories)) { }
}
$timer.Stop()
```

```powershell
$timer = [Diagnostics.Stopwatch]::StartNew(); $null = & icacls.exe $lowRoot 2>&1; $timer.Stop()
```

The timestamped follow-up repeated the first census with a stopwatch around directory enumeration,
`Get-Process`, dead-PID selection, age selection, and overlap calculation, and emitted the counts,
phase milliseconds, total milliseconds, and `[DateTime]::UtcNow.ToString('o')` as compressed JSON.

The synthetic control created 1,284 `p<hex>\fixture\repository\owner.lock` trees with
`[IO.Directory]::CreateDirectory`, deleted first all 1,284 and then the oldest 32 with
`[IO.Directory]::Delete(<validated-child>, $true)`, and finally deleted its validated
`mcg-reap-measure-<guid>` root.
