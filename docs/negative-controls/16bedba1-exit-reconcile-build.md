# Trusted build consumption in exit reconciliation

Execution owner: Acceptance. Developer compiles the changed projects and defers test execution.
All tests own their native host, compiler children, repository, canonical stores and build storage.
`ExitReconcileBuildFixture.CompleteNativeDispatch` waits for native host termination and verifies
terminal, unconsumed build evidence and authentic root/child exit artifacts before reconciliation.
The fixture's canonical state directory differs from its execution repository.

Run `ExitReconcileBuildCompositionTests`, `ExitReconcileBuildControlTests` and
`ExitReconcileBuildDispositionTests` on the exact candidate using `scripts/Invoke-TestSummary.ps1`.
Keep the existing WorkerBuildRequest authority/scope/recovery/integration tests and the completion,
NO_CHANGE, terminal-sweep and stale-reconciliation regression selections in the ordinary gate.

For each control below, preserve the tests and apparatus-failure guard. Change only the indicated
binding, compile, and execute the named unchanged test. Require a failed success assertion containing
`worker_build_host=unavailable; enrolled dispatch cannot use legacy worker receipts`. Compilation
failure, fixture setup failure, or zero tests executed does not establish the control. Restore the
binding, rebuild and require the identical selection to pass. Record candidate identity, mutation,
executed counts, RED message, restored-source identity and restored pass. Run each control separately.

| Composition | Compiling mutation | Test in ExitReconcileBuildCompositionTests |
| --- | --- | --- |
| Terminal sweep | Remove only the `WorkerBuildInvocations = new WorkerBuildInvocationOperations(...)` initializer from `TerminalGoalSweep.Run`. | `TerminalSweep_EnrolledNativeSuccess_ConsumesCanonicalBuild` |
| Batch stale recovery | In the workspace-aware `StaleDispatchProcessReconciler.Reconcile` overload, replace `OperatorCancelAwareDispatchRunner.ForWorkspace(workspace)` with `new BackgroundDispatchRunner()`. | `BatchRecovery_EnrolledNativeSuccess_ConsumesCanonicalBuild` |
| Dispatch preflight | Remove only the `WorkerBuildInvocations = new WorkerBuildInvocationOperations(...)` initializer from `GoalDispatchOperations.ReconcileExitedAssignedProcessRecords`. | `DispatchPreflight_EnrolledNativeSuccess_ConsumesCanonicalBuild` |

The positive tests must consume the genuine invocation exactly once through production composition.
The ordered controls reload settled canonical state between reconcilers, test both orders, and assert
unchanged invocation version, one verification/completion, successful task state and recovery counters.
The authority controls reject absent, prior/other-dispatch, stale-tree, invalid-scope, insufficient-project
and separate-store evidence. The separate-store controls put no invocation in the selected store while
retaining an authentic unconsumed success in the original store. No test manually stamps consumption.

`MissingConsumer_NativeSuccess_PreservesExitAndDeniesCompletion` exercises both a bare runner and
unscoped stale recovery. Its RED control is removal of the new missing-consumer failure-reason assignment
in `BackgroundDispatchRunner.ResolveWorkerBuildInvocation`: native exit preservation must fail.
The test also asserts failed verification, no successful completion/result commit, unchanged native
artifact bytes and unchanged unconsumed invocation state. Compiler failure remains a candidate failure
without the apparatus reason.

## Scoped follow-up

`TaskVerificationRecord.OrchestratorFailureReason` already denies verification success while preserving
the observed native exit. This repair uses it with `worker-build-consumer-unavailable` and preserves
the existing unavailable-consumer diagnostic and missing-build marker. The existing
`worker-build-evidence-missing` outcome is classified as `UnknownEra`; it does not provide a dedicated
infrastructure retry/attention policy for missing consumer composition. That policy needs a separate
design and acceptance scope. This patch does not extend the outcome taxonomy or authorize replay.

Cross-family Reviewer owns review of the candidate and fresh Acceptance receipts, including these
source-reverted controls and the ordinary full gate. Recovery of the supplied live occurrences remains
operator-owned after accepted repair and host activation.
