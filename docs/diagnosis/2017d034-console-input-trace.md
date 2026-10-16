# Full-screen scrolling diagnostic handoff

This candidate adds opt-in observation and deterministic test source. Cause, live baseline,
responsiveness bar and before/after improvement remain unestablished. No scrolling behavior fix
is included. The shared view belongs to scrolling goal `2017d034`; repaint implementation stays
serialized behind it, as recorded in the supplied goal timeline.

## Operator reproduction

Use an isolated candidate and a disposable long-list workspace. Leave the active console and
workers running. Launch the candidate's managed App DLL from that workspace:

```powershell
dotnet <candidate-App.dll> console --input-trace <absolute-new-json-path>
```

The parent directory must exist and the destination must be a new file. Invalid arguments,
plain/redirected mode, existing files and inaccessible destinations fail before UI startup.
The host reserves the destination before startup, captures at most 20,000 records in memory,
then exports after terminal restoration. Export failure reports `Input trace failed:` and exits 1.
The trace file is empty until exit; quit normally with `q` or cancel to export it. A killed process
does not export a receipt. Use another fresh destination for each run.

Freeze and record candidate revision, workspace/dataset, row counts in every pane, terminal
backend and dimensions, initial focus/selected identity/viewport, refresh conditions, input
sequence/cadence, repetitions, external capture resolution and the timestamp correlation method.
Include repeated arrows and Page Up/Down on long lists, mixed Home/End/Tab/Shift-Tab, command
entry/escape, first/last/empty panes, refresh/reordering and resize. Confirm the selected row and
focus after each sequence. Observe progressive partial-frame painting in the same run and
classify it as baseline or introduced; scrolling acceptance does not wait for the repaint fix.

Record external timestamped input emission and visible updates. Choose numerical latency and
accumulated-input-backlog limits from this reproduction **before** a behavior fix. Repeat the
same scenario afterward. Store the baseline/bar and later comparison in the Operator evidence
for `isolated-console-scroll-live-validation`; do not treat test results as live observation.

## Reading the trace

`Timestamp` is ticks since `ClockOrigin`; divide tick differences by `TimestampFrequency` to
obtain seconds. `OpenedUtc` anchors the session approximately to external capture; it is not
a calibrated physical-input timestamp. `Input` identifies observed keys in delivery order,
`Span` pairs begin/end records, and `Parent` identifies nested synchronous/async work. Concurrent
async actions retain separate identities. Snapshots include focus, command/modal state, pane
selection indices/identities, row counts, viewport coordinates/dimensions and board row/column
offsets. Activity identity is unavailable; its index is recorded. Typed keys are present in traces;
use disposable scenario data when entering commands.

`keyboard-callback` begins at delivery to the view; `handler` brackets routing. `headless-input`
bypasses the keyboard. Selection, viewport, fit and source-replacement records expose work
performed within that route. Refresh/startup/view Invoke records pair `posted`, `begin` and `end`
with the same span and captured input identity across the UI queue. Posted-to-begin ticks measure
that callback's queue interval; begin-to-end measures execution. An unexecuted post appears in
`IncompleteSpans`. This pairing does not establish physical-input or visible-presentation latency.
Scope completion means the bracket closed, including exceptional unwinding; it is not a success
verdict. Handler records explicitly distinguish handled/unhandled/faulted/cancelled keys, and
queued callback records mark propagated faults. Consult notices/startup errors as well.

`layout-draw-complete` records Terminal.Gui 2.5.0's application layout/draw callback. Its local
XML contract states it occurs after layout/draw when a view was processed; it does not establish
terminal flush or visible pixels. Drawing may batch multiple keys. `Input=0` is uncorrelated.
Correlate selection snapshots and the external recording; do not assign a drawing callback to
every key or call handler duration input-to-visible latency. Physical queue arrival and visible
presentation remain unknown without the external observation.

Reject measurements with `DroppedRecords > 0`, `IncompleteSpans`, missing expected callbacks,
uncorrelated external capture or changed scenarios. Trace overhead is unmeasured; compare an
untraced run as well, and reject a diagnostic run that materially changes the symptom. Avoid
speculative contention claims. Use discriminating phase evidence and a controlled one-hypothesis
comparison to authorize a narrow behavior fix, with the required independent different-family
review before acting on that diagnosis/fix.

## Acceptance handoff

Developer builds only. Acceptance executes `OwnerConsoleFullScreenInputTests`,
`OwnerConsoleModeSelectionTests`, `OwnerConsoleFullScreenTests`, `OwnerConsoleFullScreenPaneTests`,
`OwnerConsolePaneJumpTests`, `OwnerConsoleTextPageTests` and `OwnerConsoleActivityOutcomeTests`.
Use the managed runner documented in `docs/operator-runbook.md`.

New observer assertions fail when capture/correlation/overflow accounting is disabled. Mode
tests reject malformed/incompatible options and prove an existing receipt remains intact.
The diagnostic-enabled navigation test would fail without the trace; its disabled counterpart
and existing navigation tests preserve the baseline contracts. Acceptance owns execution and
negative-control receipts; none have been executed by this Developer.

The objective's separate safety checks remain Acceptance-owned:
`WorkerDispatchTestsSubscriptionPreflight.WorkerProfileDispatcherPreflightRefusesUnsupportedClaudeEffortBeforeAnyWorkerStart`
and `GoalWorktreeTests.CliLifecycleSimpleGoalRunsAcceptsAndDefersWorkspaceCleanup`
(declared in `GoalWorktreeFastTests.cs`).
These do not establish scrolling responsiveness.

Stop before changing scrolling behavior until Operator supplies baseline, numerical bar and
discriminating mechanism evidence. The full scrolling fix and live acceptance remain pending;
diagnostic preparation does not satisfy the aggregate goal.
