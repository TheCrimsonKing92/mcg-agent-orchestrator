# Dispatch grandchild console observation

Status: **FIX IMPLEMENTED — runtime acceptance pending**

## Symptom

At approximately 02:38 UTC on 2026-08-03, the dispatch-host grandchild accounting fixture left a
visible `pwsh` window on the operator desktop. The window reported Win32 `0x800700e8`
(`ERROR_NO_DATA`) while launching the fixture script, then survived until the operator terminated
pid 32264. The visible command named the fixture `burn-cpu.ps1`.

## Stdio diagnosis

The broken handle was closed early, not absent from the inheritable-handle setup. Before this fix,
the test host started the wrapper with `RedirectStandardOutput=true` and
`RedirectStandardError=true`. The test host owned the anonymous-pipe read ends; the wrapper owned
the corresponding standard-output/error write handles. Its later `Start-Process` call implicitly
offered those standard handles to the grandchild. Cancellation, test-host exit, or operator
intervention could close the owning read ends before that child launch, producing
`ERROR_NO_DATA` from the pipe-backed standard handles.

The fixture now redirects the grandchild's stdout and stderr to separate files inside its unique
fixture root. Its correctness no longer depends on the lifetime of a pipe owned by the test host.
The wrapper publishes either the child pid or a named launch-failure artifact containing the
Win32 code and full command line, so the test never waits for a process that did not start.

## Fix

- The generated script is `grandchild-reap-probe.ps1`; no compatibility alias remains.
- The CPU workload and `ownedCpuMs > 100` accounting assertion are unchanged.
- The wrapper is launched through `ProcessTreeGuiSuppression.Start`, which supplies the inherited
  hidden console used by the grandchild.
- The probe sets a self-identifying console title, appends the same identity to
  `grandchild-reap-probe.log`, and guards its stdout identity write.
- Start-gate, launch-result, probe-exit, accounting, reap, and teardown waits are bounded at 30
  seconds and report the resource whose wait expired.
- Teardown kills the recorded pid, terminates the owned process group, kills any `pwsh` whose
  command line references the unique fixture root, and asserts that the scoped match set is empty.
- The live test checks `MainWindowHandle == IntPtr.Zero` and verifies that terminating the wrapper
  also terminates the grandchild.

## Negative controls and verification

The two required pre-fix behavioral RED runs were not executed by this subscription worker. Its
task policy explicitly permits only `Invoke-WorkerBuildCheck.ps1` and forbids worker-side test
execution because raw test hosts can create per-worktree firewall prompts. On pre-fix source, the
new suppression source assertion would reject the direct `Process.Start` call, and the injected
missing-executable case would lack the named launch-failure artifact and time out waiting for a
pid. Those statements are source comparisons, not substitutes for the required behavioral RED
receipts. The acceptance runner must capture both RED receipts from the parent revision, then run
the focused green tests against this change.

The isolated worker build check passed with zero errors. No network or paid worker is used by the
fixture or its launch-failure check. `ConductorLoopHandoff.cs` and the `038b87ae` handoff path are
unchanged.
