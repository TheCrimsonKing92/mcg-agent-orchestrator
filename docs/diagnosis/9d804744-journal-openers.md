# Goal-operations journal opener audit

This audit covers every production content opener and other filesystem participant for
`.orchestrator/goal-operations/*.jsonl`. It also records test-only openers that deliberately create
contention. The implementation uses shared file handles plus bounded acquisition retry rather than a
single-writer process: it is the smaller change, reaches the conduct loop, gate-holder, Core brief builder,
and out-of-process readers, and keeps writes non-interleaving by allowing readers but excluding another
writer from the append handle.

## Enumeration method

The audit used two repository-wide passes, excluding generated output, scratch, and prototype state:

1. Search the literal `goal-operations` directory name and `JournalRoot` across source, scripts, and tests.
2. Search every consumer of `GoalOperationJournal.PathFor`, `ArchivePathFor`, `ArchiveDirectoryFor`, and
   `LifecycleIndexPath`, then trace each path to its eventual `File.*`, `FileStream`, `SharedJsonlFile`,
   `Copy-Item`, or `File.Move` operation.

Path-only consumers were checked and excluded. `GoalBoardCommand` reaches content through
`GoalOperationJournal.ReadAll`; `ConductorDriver` only constructs a path for an empty summary.

## Central open modes

`src/Mcg.AgentOrchestrator.Core/Application/SharedJsonlFile.cs` owns the in-process content handles:

- Read: `FileMode.Open`, `FileAccess.Read`, `FileShare.ReadWrite | FileShare.Delete`. The full file is read
  before parsing, so the handle lifetime does not include deserialization.
- Append: `FileMode.Append`, `FileAccess.Write`, `FileShare.Read | FileShare.Delete`. Readers and archive
  rename remain admissible, while a second writer must wait. The payload is assembled before acquisition
  and written with one `Write` call, so retry cannot duplicate a record or interleave a partial line.
- Append acquisition retries only Windows sharing/lock violations 32 and 33. The bound is five retries,
  six total open attempts, with 25 ms linear backoff (375 ms maximum total delay). Other failures surface
  immediately; the write itself is never retried.

## In-process content openers

All seven logical openers route through `SharedJsonlFile` and therefore use the modes above:

| File and symbol | Operation |
| --- | --- |
| `src/Mcg.AgentOrchestrator.App/Orchestration/GoalOperationJournal.cs` — `TryFindLifecycleGoal` | lifecycle index read |
| `src/Mcg.AgentOrchestrator.App/Orchestration/GoalOperationJournal.cs` — `RecordLifecycleGoal` | lifecycle index single-line append |
| `src/Mcg.AgentOrchestrator.App/Orchestration/GoalOperationJournal.cs` — `ReadEntries` | per-goal journal read |
| `src/Mcg.AgentOrchestrator.App/Orchestration/GoalOperationJournal.cs` — `Append` | per-goal single-line append |
| `src/Mcg.AgentOrchestrator.Core/Application/AgentOrchestratorKernel.TaskBriefs.cs` — task-brief journal projection | per-goal journal read |
| `src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/AcceptancePartitionVerdictCache.cs` — `ReadPartitionVerdictJournal` | gate-holder per-goal journal read |
| `src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/AcceptancePartitionVerdictCache.cs` — `AppendPartitionVerdictJournalEntries` | gate-holder joined multi-line append |

## Out-of-process readers and filesystem participants

| File and symbol | Access mode or disposition |
| --- | --- |
| `scripts/Extract-GoalFactsV2.ps1` — journal loop | Open/Read with `FileShare.ReadWrite | FileShare.Delete` |
| `scripts/Extract-GoalVelocity.ps1` — `Read-SharedJournalLines` | Open/Read with `FileShare.ReadWrite | FileShare.Delete`; both the content and line-count call sites use it |
| `scripts/Analyze-ExcessWorkerRounds.ps1` — `Open-SharedReadStream` | Open/Read with `FileShare.ReadWrite | FileShare.Delete`; journal content, initial hash, and revalidation hash all route through it |
| `scripts/Backup-OrchestratorState.ps1` — recursive `Copy-Item` | Copy participant with no caller-selectable share mode. It remains a documented residual; a collision is bounded by the append acquisition retry rather than swallowed |
| `src/Mcg.AgentOrchestrator.App/Orchestration/StorageRetentionMaintenance.cs` — `ArchiveGoalJournals` | `File.Move` rename participant. Shared handles permit the rename; remaining `IOException` or `UnauthorizedAccessException` is caught and deferred to the next retention sweep |

## Test-only openers

The production-like fixtures in `TaskBriefTests`, `CleanTestBaselineTests`, and
`GoalAcceptanceVerifierTests` route journal reads or appends through `SharedJsonlFile`; the cache-corruption
rewrite in `GoalAcceptanceVerifierTests` explicitly uses Create/Write with
`FileShare.ReadWrite | FileShare.Delete`.

`SharedJsonlFileTests` and `ExcessWorkerRoundAnalysisScriptTests` open deliberate Append/Write contention
handles with `FileShare.Read` or `FileShare.Read | FileShare.Delete`. They intentionally exclude another
writer so the bounded production acquisition path is exercised deterministically.
`GoalOperationJournalContentionTests` instead holds Open/Read with
`FileShare.ReadWrite | FileShare.Delete`, proving the production appender remains admissible. Fixture setup
and post-condition reads use convenience helpers only when no concurrent production operation exists.
These handles are test controls, not runtime journal access paths. The CLI and retention tests returned by
the path-helper search either call the production journal API or only compare, delete, or check a path; they
introduce no additional content opener.

## Structural conclusion

No production content path uses `File.ReadLines`, `File.AppendAllText`, `File.AppendAllLines`, or
`Get-Content` directly on a goal-operations journal. Content access is centralized in `SharedJsonlFile` for
C# and explicit shared-read helpers for PowerShell. The only non-content participants are the documented
archive rename and backup copy operations above.
