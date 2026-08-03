# Max-duration handoff console observation

Status: **CONFIRMED — fixed and verified**

The visible-console flash is on the Windows max-duration handoff's direct `CreateProcessW` path,
not `Start-OrchestratorCommand.ps1`. The permanent pre-spawn diagnostic now distinguishes a
window handle (`GetConsoleWindow`) from actual console attachment (`GetConsoleProcessList`) and
records visibility as well. This distinction matters: `CreateNoWindow` can report no console
window while the process remains attached to a windowless console.

## Pre-fix observations and gate decision

These observations used the real
`conduct --loop --daemon --watch --poll-seconds 1 --max-duration 3 --quiet` path and the apphost
launched with `DETACHED_PROCESS`, making the unattached state reachable. The detector synchronously
polled all visible top-level windows every 25 ms; it does not depend on WinEvent callbacks or a
message pump. Before each run, its positive control launched `cmd.exe` with `CREATE_NEW_CONSOLE`
and detected one `ConsoleWindowClass` window.

| Handoff | Pre-spawn UTC | Incumbent window | Attached | Visible | New successor console window |
| --- | --- | --- | --- | --- | --- |
| pre-fix 1 | 04:08:11.866 | absent | false | false | **Yes** — `ConsoleWindowClass`, apphost title, detected at 04:08:11.964 (+98 ms) |
| pre-fix 2 | 04:08:17.680 | present | true | true | No |
| pre-fix 3 | 04:08:23.672 | present | true | true | No |

The hypothesis is **CONFIRMED**, with attachment as the causal state: the unattached incumbent's
handoff allocated the new visible console, while the two already-attached incumbents inherited the
existing console and allocated no additional console window. `GetConsoleWindow()==0` alone is not
a sufficient attachment probe; the shipped record retains both measurements.

Only after this result was recorded was the suppression change applied.

## Fix

`ConductorLoopHandoff.LaunchDetachedWindows` now acquires the existing
`ProcessTreeGuiSuppression` hidden-console scope when the launching process has no console window,
keeps that scope alive through `CreateProcessW`, and then disposes it. An interactive incumbent's
existing visible console is preserved. Acquisition failure emits the typed
`loop-handoff-console-suppression-failed` event with error type and Win32 code, then performs the
handoff without suppression.

The process contract is unchanged:

- `bInheritHandles` remains `true`.
- `STARTF_USESTDHANDLES` and the explicit stdout/stderr handle list remain in use.
- Creation flags are pinned exactly to `0x01080600`: `CreateBreakawayFromJob |
  CreateNewProcessGroup | CreateUnicodeEnvironment | ExtendedStartupInfoPresent`.
- `CreateNoWindow` and `DetachedProcess` remain absent.
- The non-Windows branch and `Start-OrchestratorCommand.ps1` are unchanged.

## Post-fix observations

The same positive-controlled detector ran against the fixed binary. A no-op worker was executed
through `DispatchProcessHost` while the first conductor generation was alive; host exit and worker
exit artifact were both `0`, and no paid worker was started.

| Handoff | Pre-spawn UTC | Incumbent window | Attached | Spawn attached | Spawn visible | Suppression | New console windows |
| --- | --- | --- | --- | --- | --- | --- | --- | ---: |
| post-fix 1 | 04:17:35.958 | absent | false | true | false | hidden-console-acquired | 0 |
| post-fix 2 | 04:17:41.441 | present | true | true | false | existing-console-preserved | 0 |
| post-fix 3 | 04:17:46.732 | present | true | true | false | existing-console-preserved | 0 |

The detector's `CREATE_NEW_CONSOLE` positive control found one visible console immediately before
this sequence; the three real handoffs found zero new visible top-level windows. A separate
three-handoff run at 04:16 UTC also found zero windows, and a full-desktop capture after the third
handoff was visually inspected with no console window present.

## Regression controls

The new assertions were demonstrated RED before their production seams:

- Exact creation flags: build failed with `CS0117` because
  `ConductorLoopHandoff.WindowsSuccessorCreationFlags` did not yet exist.
- Attachment diagnostic: the focused runtime test failed because
  `incumbentConsoleAttached=(true|false)` was absent.
- Suppression lifetime and fail-open seams: build failed with `CS1739` because the
  `acquireConsoleSuppression` parameter did not yet exist.

After the fix, the focused `*ConductorLoopHandoff*` MTP run executed 17 tests: 17 passed, 0 failed.
The complete `ConductorBatchLoopTests` class then executed 187 tests: 187 passed, 0 failed.
