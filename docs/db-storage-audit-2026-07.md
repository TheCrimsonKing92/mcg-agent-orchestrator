# SQLite Storage Audit - 2026-07

## Summary

The immediate storage problem is `.orchestrator/run-events.db`, not the durable goal state store. Operator survey on 2026-07-16 found:

| Store | Size | Current assessment |
| --- | ---: | --- |
| `.orchestrator/run-events.db` | 1,824 MB survey; 1,833,816,064 bytes measured on this machine before this increment | Oversized high-churn journal. `conductor.tick` rows carry full per-tick board snapshots. |
| `.orchestrator/state.db` | 87 MB survey; 87,056,384 bytes measured | Follow-up. Contains 583 goals with full timelines; this goal does not touch its schema or goal records. |
| `.orchestrator/backlog.db` | 1 MB survey; 1,024,000 bytes measured | Fine today. |
| `.orchestrator/collaboration-items.db` | 3 MB survey; 3,059,712 bytes measured | Fine today. |
| `.orchestrator/dogfood-log.db` | 0.5 MB survey; 536,576 bytes measured | Fine today. |

The measured baseline also found one `run_events` table with `(seq, event_id, occurred_at, event_type, goal_id, operation, status, detail, payload_json)`, 147,306 rows spanning 2026-06-21 through 2026-07-16. `conductor.tick` accounted for 23,659 rows and 1,628 MB of `payload_json` at about 70 KB per tick, or 97% of the file. `goal.operation` accounted for 123,672 rows and 47 MB.

## Consumer Map

`run_events` readers:

| Consumer | Source | Reads | Retention need |
| --- | --- | --- | --- |
| Local `goals subscribe` / `monitor-goal --from-cursor` | `GoalMonitoringSubscriptionCommand` | `goal.operation` records after a run-event cursor, filtered by goal and event kind. Uses `seq`, `event_type`, `goal_id`, `operation`, `status`, `detail`, and artifact path. Does not require `conductor.tick` payload history. | Preserve `goal.operation` rows for long-lived provenance/cursor use. Tick pruning must not delete goal operation rows or break sequence gaps. |
| Dashboard SSE goal stream | `GoalMonitoringStream` and `DashboardMonitoringEvents` | Recent `conductor.tick` rows after an in-memory stream cursor, max 200 per poll. Uses tick counters and bounded `progressLines` for live stream events. | Recent ticks only. Historical full snapshots are not needed. |
| Dashboard/reports operator disposition hint | `ConductorOperatorDispositionSnapshots.TryReadLatestForGoal`, `DashboardEndpoints.Goals`, `DashboardEndpoints.Reports`, `GoalMonitoringStream` | Previously scanned the first 2,000 rows from the start of `run_events` and tried to deserialize `operatorDispositions` from `conductor.tick` payloads. If no snapshot is available, callers already compute a live disposition from the current goal state. | Full historical board snapshots are not required. This increment stops persisting them and relies on live recomputation. |
| Loop handoff evidence | `ConductorLoopHandoff.TryRecordHandoffEvent` | Appends `conductor.tick`-typed lifecycle markers with small detail. | Keep as tick rows subject to high-churn retention unless promoted to a separate event type later. |
| Goal operation provenance | `GoalOperationJournal` and subscribe/provenance surfaces | Appends and reads compact `goal.operation` events. | Keep longer than tick rows. No pruning implemented in this increment. |

`state.db` goal timeline readers are broad: CLI timeline/task views, dashboard detail and reports, monitor snapshots, acceptance/readiness/recovery planners, subscription brief construction, and lifecycle/projection code all read full goal records and timelines through the kernel/repository. Because these consumers treat goal timelines as durable audit state, this increment does not change `state.db` structure, retention, or serialization.

## Root Cause

The tick writer is `ConductorTickPusher.TryRecord`. Before this increment it serialized:

- tick counters,
- all progress lines for the tick,
- `operatorDispositions` for the entire board.

The disposition snapshots include per-goal reasons, blockers, evidence pointers, and dispatch disposition lists. During conductor watch loops this produced a full board snapshot on every tick, so growth scaled with both tick cadence and number of tracked goals. With 23,659 tick rows carrying 1,628 MB, the observed average was about 70 KB per tick.

The implemented writer now persists only tick counters, bounded progress lines, and `operatorDispositionCount`. It does not persist the full `operatorDispositions` array. Live dashboard snapshot code already computes current dispositions when no persisted snapshot is present.

## Implemented Increment

Implemented in this goal:

- `ConductorTickPusher` persists bounded tick JSON: at most 32 progress lines, each at most 240 characters, plus counters and `operatorDispositionCount`.
- `SqliteRunEventStore.MaintainAsync` prunes old `conductor.tick` rows with an age window and minimum recent-row floor. It does not prune `goal.operation`.
- Maintenance uses `BEGIN IMMEDIATE` with zero busy timeout and returns `deferredReason=database-busy` instead of waiting behind an active writer. Tick append behavior remains advisory and unchanged.
- Optional `VACUUM` is explicit and runs after the prune transaction; if vacuum hits a transient lock, pruning remains committed and the result reports `vacuumDeferred=True`.
- CLI verb: `run-events-maintenance [--tick-max-age-days <days>] [--keep-tick-rows <count>] [--vacuum]`.

Not implemented in this goal:

- Any `state.db` schema or goal-record change.
- `goal.operation` retention.
- A separate latest-disposition table.
- Payload compression or externalized blobs.
- Automatic maintenance from the conductor tick path.
- A migration that rewrites existing retained tick payloads in place.

## Supersession note — 2026-09-02

Later conductor work now schedules the run-event maintenance path daily, requests a weekly off-peak `VACUUM`, and prunes aged `goal.operation` rows only for goals whose persisted state is terminal. The historical list above remains scoped to the July goal.

Generated-evidence maintenance now runs under a cross-process sweep lease and emits policy-versioned `retention.reclamation` run events. It applies terminal-owner, live-attempt, age, byte, and count decisions to dispatch logs plus acceptance and pre-review attempt trees; prompt deletion additionally requires an exact persisted `PromptPath`. Ambiguous or unrecorded ownership retains, goal-event JSONL remains raw for replay, and partial file-lock failures are recorded per path.

MTP test runs created after this change carry an atomic `.mtp-run-ownership.json` sidecar. Existing failed-run directories remain undecidable and are not swept: their goal ownership was never recorded, so deleting them would violate the fail-closed policy.

## Target Structure

Recommended follow-up increments:

1. Split high-churn run telemetry from durable provenance: keep `goal.operation` as durable evidence and move tick telemetry to a retention-classed table or database.
2. Add retention classes by event type: `conductor.tick` short age/count window, handoff/control events medium window, `goal.operation` longer audit window.
3. Add a latest-only disposition cache keyed by goal id if dashboard disposition recomputation proves expensive or semantically different from conductor-owned disposition.
4. Review `ReadSinceAsync` query patterns and add a `(goal_id, event_type, seq)` index only if subscribe filtering shows measurable pressure.
5. Evaluate `auto_vacuum=INCREMENTAL` for new run-event databases. Existing databases require a rebuild/VACUUM migration to enable it effectively.
6. Tackle `state.db` separately: timeline paging or summary tables for 583-goal walks, without weakening the append-only audit invariant.

## Growth Model

Observed pre-fix tick payload growth:

- 1,628 MB / 25 days = about 65 MB/day from `conductor.tick` payloads alone.
- At 23,659 ticks / 25 days = about 946 ticks/day.
- About 70 KB/tick observed average.

Post-fix bounded worst case:

- Max persisted progress text is 32 * 240 characters plus JSON overhead, under 10 KB in the pinned test even with oversized input.
- At the observed 946 ticks/day, worst-case tick payload growth is under about 9.5 MB/day.
- Typical growth should be much lower because most ticks have far fewer than 32 progress lines.

Event-driven wake may increase tick frequency. At 5,000 ticks/day, the old average would add about 350 MB/day. The new bounded worst case stays under about 50 MB/day before retention, and the 7-day / 5,000-row default retention caps retained tick rows. For aggressive reclamation after the existing 1.8 GB buildup, use a shorter one-time window such as `--tick-max-age-days 3 --keep-tick-rows 2000 --vacuum` during an idle period, then return to the default window.

## Reclamation Receipt

Pre-maintenance size measured on this machine:

- `C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator\run-events.db` = 1,833,816,064 bytes at first survey during this task.
- The live file grew to 1,837,817,856 bytes by command execution, confirming old tick payload growth was still active.

Maintenance receipts:

1. `run-events-maintenance --tick-max-age-days 3 --keep-tick-rows 2000 --vacuum`
   - `status=completed`
   - `conductorTickRowsDeleted=19826`
   - command-reported `bytesBefore=1837817856 bytesAfter=1743290568`
   - `vacuumCompleted=True vacuumDeferred=False`
2. `run-events-maintenance --tick-max-age-days 1 --keep-tick-rows 500 --vacuum`
   - `status=completed`
   - `conductorTickRowsDeleted=2175`
   - command-reported `bytesBefore=868671488 bytesAfter=912532032`; this includes transient WAL/SHM accounting during maintenance, so the direct file check below is the authoritative post-run receipt.
   - `vacuumCompleted=True vacuumDeferred=False`

Post-maintenance direct file check:

- `C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator\run-events.db` = 454,877,184 bytes.
- No `run-events.db-wal` or `run-events.db-shm` file was present in the post-run direct check.
- A final check later in the same worker turn measured 455,512,064 bytes, showing the pre-fix live writer was still adding tick payloads while this branch had not yet landed.
