# Worker-dispatch fixture behavioral RED/GREEN specification

## Evidence status and measured target

This is the Developer-owned mutation specification required by the amended criterion 1. Neither
RED arm nor the GREEN arm below was executed: subscription workers cannot launch a .NET test host.
The test-capable operator/acceptance boundary owns applying each mutation, executing the commands,
recording result counts and exact assertion output, retaining the TRX files, and reverting the
mutation before GREEN. A compile receipt is not behavioral RED/GREEN evidence.

Lane membership was read from `config/acceptance-manifest.json` at HEAD:
`RealWorkerProcessGuardTests`, `WorkerDispatchTestsDispatchPreparation`,
`WorkerDispatchTestsModelSelectionEnvMutation`, `WorkerDispatchTestsSandboxLowIntegrity`,
`WorkerDispatchTestsSubscriptionPreflight`, `WorkerDispatchTestsWorkerResultClassification`, and
`WorkerProcessJobsTests`. The manifest is unchanged by this goal.

For receipts written at or after `1f669450`, qualification required a sibling
`<attempt-id>.attempt.json`, `state == "completed"`, `exitCode == 0`, and parseable heartbeat
`startedAt`/`lastObservedAt` values. This yielded 274 qualified and 101 excluded process
heartbeats. All 10 qualified worker-dispatch lane TRXs joined to their process receipts. The lane
measured 392.8s mean with a 311.9-508.9s range; its joined TRXs attribute about 287.5s per run to
`WorkerDispatchTestsWorkerResultClassification`, versus about 41.0s for the next class.

The change removes 25 deterministic five-second exit polls (125s of serial class time) and replaces
five Git process launches per seeded fixture with one test-host template plus isolated filesystem
copies. The seven lane classes contain 106 static seeded-fixture call sites and execute at least 109
fixture constructions per lane run. Expected process-wall reduction: **200-250s**. The operator
must report both post-change mean and range; a reduction below the 197.0s baseline spread remains
unproven.

## Control 1: every reaped hung/stalled wrapper requests termination

### Reached path and exact production mutation

Every case below reaches `BackgroundDispatchRunner.ReapTrackedProcessJobs` through
`RefreshLatestProcess`, or through `ReconcileLatestProcess` followed by
`ApplyRefreshOutcomeAndWriteDiagnostics`. In
`src/Mcg.AgentOrchestrator.Infrastructure/Processes/BackgroundDispatchRunner.cs`, method
`ReapTrackedProcessJobs`, current line 4311, apply this exact hunk:

```diff
@@
-            _tryKillOwnedProcess(processId);
+            // NEGATIVE CONTROL: omit the no-job-accounting termination request.
```

Do not mutate the neighbouring `WorkerProcessJobs.Reap` call: these tests exercise the fallback
that runs when no real job-accounting object exists.

### Test identities and expected RED output

All identities are in the global namespace and begin with
`WorkerDispatchTestsWorkerResultClassification.`. Run the whole class once for this mutation; it
should discover the unchanged 151 cases and fail the 25 rows listed here.

| Fully qualified test identity and rows | Assertion reached | Expected RED output |
| --- | --- | --- |
| `WorkerDispatchTestsWorkerResultClassification.BackgroundDispatchRunnerRefreshFailsIdleCodexWrapperAfterFinalOutput` | test file line 909 | `Assert.True() Failure`, expected `True`, actual `False` |
| `WorkerDispatchTestsWorkerResultClassification.BackgroundDispatchRunnerRetriesFailedUnchangedProcessLogRead` | test file line 1065 | `Assert.True() Failure`, expected `True`, actual `False` |
| `WorkerDispatchTestsWorkerResultClassification.BackgroundDispatchRunnerRefreshUsesProviderIdentityForCodexExitFileBehavior` row `("codex-cli", "subscription worker prompt", true)` | test file line 1167 | `Assert.Equal() Failure: Values differ`, expected `True`, actual `False` |
| `WorkerDispatchTestsWorkerResultClassification.BackgroundDispatchRunnerRefreshFailsProviderNeutralStallAfterHeartbeatProgressTimeout` | test file line 1213 | `Assert.True() Failure`, expected `True`, actual `False` |
| `WorkerDispatchTestsWorkerResultClassification.BackgroundDispatchRunnerStartupHangFastPathFiresWhenCpuIdleAndNoOutput` | test file line 1436 | `Assert.True() Failure`, expected `True`, actual `False` |
| `WorkerDispatchTestsWorkerResultClassification.BackgroundDispatchRunnerHungWrappersCompleteReadOnlyRolesWithValidWorkerResult` rows `(Planner,true)`, `(Planner,false)`, `(Researcher,true)`, `(Researcher,false)`, `(Reviewer,true)`, `(Reviewer,false)` | shared reached helper line 4221 | `Assert.True() Failure`, expected `True`, actual `False` |
| `WorkerDispatchTestsWorkerResultClassification.BackgroundDispatchRunnerHungWrapperChildZeroCompletesVerificationOnlyTester` rows `(true)`, `(false)` | shared reached helper line 4221 | `Assert.True() Failure`, expected `True`, actual `False` |
| `WorkerDispatchTestsWorkerResultClassification.BackgroundDispatchRunnerHungWrappersFailReadOnlyRolesWithoutValidWorkerResult` rows `(true,"empty")`, `(false,"empty")`, `(true,"malformed")`, `(false,"malformed")`, `(true,"blocked")`, `(false,"blocked")` | shared reached helper line 4221 | `Assert.True() Failure`, expected `True`, actual `False` |
| `WorkerDispatchTestsWorkerResultClassification.BackgroundDispatchRunnerReadOnlyHungWrapperRescueIsDispatchAgnostic` | shared reached helper line 4221 | `Assert.True() Failure`, expected `True`, actual `False` |
| `WorkerDispatchTestsWorkerResultClassification.BackgroundDispatchRunnerHungWrapperCompletesWhenFileRoleWorktreeEvidencePasses` rows `(Developer)`, `(Tester)` | test file line 2036 | `Assert.True() Failure`, expected `True`, actual `False` |
| `WorkerDispatchTestsWorkerResultClassification.BackgroundDispatchRunnerHungClaudeCliWrapperCompletesWhenWorktreeEvidencePasses` | test file line 2088 | `Assert.True() Failure`, expected `True`, actual `False` |
| `WorkerDispatchTestsWorkerResultClassification.BackgroundDispatchRunnerHungWrapperFailsWhenFileRoleWorktreeEvidenceMissing` rows `(Developer)`, `(Tester)` | test file line 2139 | `Assert.True() Failure`, expected `True`, actual `False` |

The 25 count is the sum of the rows above, not an estimate. A RED that fails earlier for another
reason does not discriminate this behavior and must not be accepted as this control.

## Control 2: provider identity keeps non-Codex dispatches out of the Codex exit-file path

### Reached path and exact production mutation

The two negative rows below reach `TryDetectHungCodexWrapper`, which calls
`UsesCodexExitFileBehavior`. In
`src/Mcg.AgentOrchestrator.Infrastructure/Processes/BackgroundDispatchRunner.cs`, method
`UsesCodexExitFileBehavior`, current lines 4026-4027, apply this exact hunk:

```diff
@@
-    private bool UsesCodexExitFileBehavior(TaskDispatchRecord? dispatch) =>
-        dispatch is not null && ResolveWorkerProvider(dispatch).Identity.UsesCodexExitFileBehavior;
+    private bool UsesCodexExitFileBehavior(TaskDispatchRecord? dispatch) =>
+        dispatch is not null;
```

### Test identity and expected RED output

Run the single theory identity. These two rows must fail at test file line 1166 before the later
termination-request assertion:

| Fully qualified test identity and rows | Expected RED output |
| --- | --- |
| `WorkerDispatchTestsWorkerResultClassification.BackgroundDispatchRunnerRefreshUsesProviderIdentityForCodexExitFileBehavior` row `("claude-cli", "claude prompt", false)` | `Assert.Equal() Failure: Values differ`, expected `False`, actual `True` |
| `WorkerDispatchTestsWorkerResultClassification.BackgroundDispatchRunnerRefreshUsesProviderIdentityForCodexExitFileBehavior` row `("custom-agent", "codex exec prompt", false)` | `Assert.Equal() Failure: Values differ`, expected `False`, actual `True` |

The unchanged Codex row should remain GREEN under this mutation. This control proves the negative
provider path separately from control 1's termination fallback.

## Operator execution and GREEN

Apply only one mutation at a time in a disposable checkout. Use a reverse patch to restore the
mutation, confirm the checkout is clean, then run GREEN against the final candidate. At this HEAD,
`Invoke-TestSummary.ps1` retains failed RED result directories but removes clean GREEN result
directories and has no `-RetainSuccessfulArtifacts` parameter. Preserve each RED TRX and record the
GREEN terminal summary and exit code. The focused commands are:

```powershell
# Control 1 RED and final GREEN: expected unmutated discovery is 151, all passing.
.\scripts\Invoke-TestSummary.ps1 -Target .\tests\Mcg.AgentOrchestrator.Infrastructure.Tests\Mcg.AgentOrchestrator.Infrastructure.Tests.csproj -Filter 'FullyQualifiedName~WorkerDispatchTestsWorkerResultClassification'

# Control 2 RED: expected unmutated discovery is 3, all passing.
.\scripts\Invoke-TestSummary.ps1 -Target .\tests\Mcg.AgentOrchestrator.Infrastructure.Tests\Mcg.AgentOrchestrator.Infrastructure.Tests.csproj -Filter 'FullyQualifiedName~WorkerDispatchTestsWorkerResultClassification.BackgroundDispatchRunnerRefreshUsesProviderIdentityForCodexExitFileBehavior'
```

Record command, candidate SHA, mutation hunk, exit code, discovered/passed/failed counts, exact
assertion output, and the retained TRX path for each RED arm; for GREEN, record the successful
terminal summary and the runner's clean-result removal. At Developer handoff the durable status is:
**RED not executed; GREEN not executed; operator owns both executions.**

## Assertion-semantics accounting

No assertion was removed. The production-outcome assertions in all affected tests remain intact;
the new lifecycle assertions strengthen them by proving the runner requested termination before
the signal-backed fake reports that the process stopped. The seeded-repository change is confined
to test fixture construction in `WorkerDispatchTestSupport`; it changes no test identity or
assertion. Reviewer owns the independent assertion-count and condition comparison.
