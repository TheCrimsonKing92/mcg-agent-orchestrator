# Test-temp inventory and reclamation handoff

This is an operator-owned, post-landing procedure. The inventory script is read-only; it never deletes a directory or lease. A worker run is not evidence of real-machine cleanup.

## 1. Stop relevant work

1. From the repository root, run `./scripts/Invoke-RepoScript.ps1 scripts/Get-OrchestratorSnapshot.ps1` and wait for acceptance gates and test workers to reach a quiet window.
2. Follow the deliberate-stop procedure in [operator-runbook.md](operator-runbook.md#operating-the-goal-loop): create `.conduct-stop`, wait for the conductor's `LOOP_STOP` receipt, and retain the recorded conductor PID/start evidence. If an exact stale test process remains, use `./scripts/Invoke-RepoScript.ps1 scripts/Stop-RepoProcess.ps1 -Id <exact-pid> -CommandContains <expected-command-fragment>`; never stop a process by name.
3. Do not remove `.conduct-stop` until inventory and any separately authorized reclamation are finished.

## 2. Run the bounded dry-run inventory

Run:

```powershell
./scripts/Invoke-RepoScript.ps1 scripts/Get-TestTempRootInventory.ps1 -IncludeRepoFallbackRoots -MaxCandidates 256 -MaxFiles 250000
```

For additional fallback outputs outside this worktree, repeat with exact `-Root <path>` arguments. Add `-NoDefaultRoots` when an exact-root batch must exclude the standard LocalAppData root. The receipt reports the inspected candidate count, measured files/bytes, whether the measurement hit either bound, and the dry-run `Reclaimable` set. Rerun in bounded batches with exact roots if `MeasurementComplete` is false.

The classifier fails closed:

- `Live` and `LiveOrLocked` roots are preserved.
- missing, unreadable, malformed, mismatched, inaccessible, or changed identities are `Ambiguous` and preserved.
- only a `p{hex}` root with a valid pid/start-time lease, a dead or reused process identity, and a second identical identity read while holding the lease exclusively is `Reclaimable`.
- legacy flat directories are `Ambiguous`; age alone never authorizes deletion.

Archive the console receipt before making a decision. It is the required read-only count/byte and reclaimable-list evidence; `MutationPerformed` must be `False`.

## 3. Separate deletion decision

Actual deletion requires a new explicit operator authorization naming the exact `Reclaimable` paths from a fresh inventory receipt. This repository intentionally supplies no bulk-delete command here. Revalidate each candidate immediately before a consequential action using the same pid/start-time and exclusive-lease checks; if any check changes or fails, preserve it. Report selected path count and bytes before and after the authorized operation.

Afterward, remove `.conduct-stop` and resume through the runbook's normal conductor launch. `scripts/Resume-OrchestratorLoop.ps1` is suitable only after the deliberate-stop marker has been removed and its journaled command is the intended one.
