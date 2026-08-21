# Infrastructure lane fixed-cost attribution

## Attribution

The removable per-process cost is the synchronous orphan-root reap in
`AssemblyTempRedirect.Install`: every Infrastructure test host walks the accumulated real
`%LOCALAPPDATA%\Temp\Low\mcg-tests` population before discovery and previously attempted every
dead-PID root and every age-expired root as separate sets. The `icacls` setter timeout is not the
observed mechanism.

This attribution is based on scale discrimination, not the coincidence between 119.1 seconds and
the two-minute setter bound. On 2026-08-21 the live root had 1,293 PID-shaped roots, 1,284 with no
live PID, 1,213 older than twelve hours, and 1,211 selected by both rules. A read-only recursive
walk of all 1,293 real roots took 41.6 seconds; the same walk with file metadata inspection did not
finish inside 120 seconds. No individual root took one second or contained 10,000 entries, so the
cost is aggregate fanout across the shared population rather than one pathological tree.

The negative control was a synthetic population with the same 1,284-root count, one nested fixture
and one file per root. Deleting all of it took 714 ms; deleting 32 took 59 ms. Root count by itself
therefore does not explain the floor. The expensive input is the accumulated content of the real
shared population, traversed synchronously and, for 1,211 roots, eligible through both rules.

## Timing breakdown

| Phase or control | Elapsed | Outcome |
| --- | ---: | --- |
| Fastest supplied `shard-complete` receipt | 119,100 ms | Lane total; raw receipt is not present in this worktree |
| Declared work for the matching small lane | 8,600 ms | Supplied estimate |
| `icacls` query of the live Low root | 15 ms | Completed; valid inheritable Low label |
| Live sibling census and dead/old selection | 213 ms | 1,293 owned; 1,284 dead; 1,213 old; 1,211 overlap |
| One read-only recursive walk of all live owned roots | 41,600 ms | Completed; no root exceeded 1,000 ms |
| Recursive walk plus per-file metadata | >120,000 ms | Command deadline fired before completion |
| Delete representative synthetic 1,284-root population | 714 ms | Completed |
| Delete representative synthetic bounded set | 59 ms | 32 completed |

The conservative residual using only the completed live recursive-walk measurement is:

`total_lane - measured_live_walk = 119.1s - 41.6s = 77.5s unattributed`.

The permanent `assembly-temp-redirect-timing` stderr receipt added by this change closes that
residual on the real Low host. It records total initialization, create, label query, typed label-set
outcome (`completed`, `timedOut`, `exitCode`, success and failure kind), probe write/cleanup,
sibling enumeration, process snapshot, both selections, overlap, bounded selection, deletion
attempts/successes, and deletion elapsed time. The receipt is emitted on the existing per-lane
stderr stream and creates no shared file.

## Why the setter hypothesis is excluded

1. A fired `SetIntegrity` deadline costs at least 120.0 seconds inside module initialization, but
   the fastest supplied lane is 119.1 seconds including discovery and test work.
2. The two normal LOCALAPPDATA candidate sources resolve to the same path. A failed first candidate
   would repeat query/set/query on the second, predicting approximately 240 seconds.
3. The live root has a parser-compatible inheritable Low label and its read-only query completed in
   15 ms. The known historical 120-second failure used recursive `/T` labeling with undrained pipes;
   the current setter is non-recursive and drains both streams.

`IcaclsIntegrityLabeler` now preserves its Boolean contract but also exposes the last setter's typed
diagnostic outcome. This instrumentation does not change the timeout, label, inheritance flags, or
fallback decision.

## Remedy and criterion 2 handoff

The reap now unions the dead-PID and age-expired sets case-insensitively, sorts them oldest first,
and attempts at most 32 roots per process. Both abandonment rules remain active, process-exit
cleanup remains active, and failed deletes remain best effort. This bounds the synchronous critical
path without weakening Low-integrity confinement or adding a shared lock.

The synthetic control predicts a bounded-set reduction from 714 ms to 59 ms for a shallow
1,284-root population. Criterion 2 still requires the operator-owned real-world receipt: compare the
same named lane on uncontended before/after gates, record the shared-root population at both points,
and recover `assembly-temp-redirect-timing` from lane stderr. Do not clear the shared root between
runs. Two concurrent gates are not comparable.

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

The synthetic control created 1,284 `p<hex>\fixture\repository\owner.lock` trees with
`[IO.Directory]::CreateDirectory`, deleted first all 1,284 and then the oldest 32 with
`[IO.Directory]::Delete(<validated-child>, $true)`, and finally deleted its validated
`mcg-reap-measure-<guid>` root.

