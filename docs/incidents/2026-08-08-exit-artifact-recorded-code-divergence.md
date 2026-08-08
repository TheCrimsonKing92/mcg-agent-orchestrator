# Exit artifact / recorded-code divergence investigation

Goal `707d4244` investigated the two Planner completions from historical goal `94fdcb8a` where the worker-authored exit artifact contained `0` but the recorded verification exit code was `1`.

## Finding

The retained evidence available in this worktree does not discriminate between two surviving assignment sites. The historical assignment is therefore unproven; naming either site as certain would be a guess.

1. `src/Mcg.AgentOrchestrator.Infrastructure/Processes/BackgroundDispatchRunner.cs:1217` is the leading candidate. It writes `exitCode = 1` when `PlannerOutputContract.Resolve(...)` rejects the captured Planner output or returns no plan. Its diagnostic starts with `Planner output contract failed`. The real completion-path test `IncompleteContract_KnownWrongArtifactZeroRecordedCodeOne` proves that a pre-existing artifact containing `0` remains `0` while `TaskVerificationRecord.ExitCode` becomes `1`.
2. `src/Mcg.AgentOrchestrator.Infrastructure/Processes/BackgroundDispatchRunner.cs:1231` remains reachable. It writes `exitCode = 1` after the plan resolves successfully but `PlannerOutputContract.TryPersistDurableReceipt(...)` cannot append the canonical plan receipt to captured stdout. Its diagnostic starts with `Planner output contract could not persist the accepted plan`, so a search only for `Planner output contract failed` cannot eliminate it.

Both writes leave the `orchestratorFailureReason` parameter null. Consequently the restore at lines 1421-1426 does not run. Because `exitArtifactAlreadyExisted` is true, lines 1436-1443 do not overwrite the worker artifact, and line 1450 plus the verification construction at line 1472 record the mutated value.

To discriminate the historical rounds, retain either `TaskVerificationRecord.StandardError` (the two branches produce distinct diagnostics) or an explicit completion event containing the selected contract branch, the observed artifact exit code, the final recorded exit code, and `OrchestratorFailureReason`. The copied context contains neither the goal event log nor the task verification/error artifacts.

No production fix was applied. Choosing whether an orchestrator-authored Planner contract failure should override a clean worker exit is a policy decision; merely setting `orchestratorFailureReason` would cause the existing restore to discard the deliberate contract failure. Classification and label-matching logic were not changed, and the observed frequency labels were not used as causal evidence.

Verification state: the characterization test asserts the current divergent behavior (it is not skipped). The subscription lane's sanctioned `Invoke-WorkerBuildCheck.ps1` completed with zero errors. Test execution and the RED/GREEN negative control are explicitly deferred to the test-capable acceptance lane by `docs/negative-controls/707d4244.md`; no test-pass claim is inferred from compilation.

## Exhaustive `exitCode` write inventory

Derived with:

```text
rg -n -P "^\s*(?:var\s+)?exitCode\s*=(?!=)" src/Mcg.AgentOrchestrator.Infrastructure/Processes/BackgroundDispatchRunner.cs
```

Writes after the artifact-derived initialization at line 818:

| Line | Value/condition | Sets `orchestratorFailureReason`? | Covered by 1421-1426? |
|---:|---|---|---|
| 1187 | Researcher contract rejected/no research | No | No |
| 1197 | Accepted Researcher artifact could not be persisted | No | No |
| 1217 | Planner contract rejected/no plan | No | No |
| 1231 | Accepted Planner plan could not be persisted | No | No |
| 1280 | Deterministic worker build check failed | No | No |
| 1315 | Orchestrator successfully committed verified edits | No; writes zero | No |
| 1341 | Dirty worktree could not be landed while current code was zero | No | No |
| 1376 | Required file-change evidence absent | No | No |
| 1402 | Worktree inspection unavailable with `git-inspection-failed` | No | No |
| 1425 | Restore to `observedExitCode` | Guarded by a reason passed into the method | This is the restore |
| 1601 | Hung-wrapper completion starts at one (separate method) | No | No |
| 1606 | Hung-wrapper rescue changes it to zero (separate method) | No | No |
| 1637 | Suspected-hang completion initializes from a valid artifact, otherwise one (separate method) | `detectorDiagnostic` is assigned at 1641 | Yes unless rescue clears it |
| 1645 | Hung-wrapper rescue restores valid artifact code, otherwise zero (separate method) | Cleared at 1648 | No restore needed |

The non-zero main-completion writes not covered by the restore are 1187, 1197, 1217, 1231, 1280, 1341, 1376, and 1402. For a Planner dispatch, `RequiresFileChangeEvidence` at lines 1584-1588 is false, so the worktree block containing 1280, 1315, 1341, 1376, and 1402 is unreachable. The Researcher-only block excludes 1187 and 1197. That leaves 1217 and 1231.

## Re-check of the proposed eliminations

### Worktree inspection failure (line 1402): confirmed, but by reachability

The requested historical search was attempted from the assigned working directory:

```text
rg -n -F "unavailable_reason=git-inspection-failed" .orchestrator -g '!**/bin/**' -g '!**/obj/**' -g '!**/.scratch/**' -g '!**/.orchestrator-prototype/**'
rg: .orchestrator: IO error for operation on .orchestrator: The system cannot find the file specified. (os error 2)
```

The absence search cannot support the elimination because `.orchestrator` is not present. Positive source evidence does: lines 1245-1251 call `InspectGoalWorktree` only when `RequiresFileChangeEvidence(task)` is true, and lines 1584-1588 limit that predicate to non-local Developer or Tester dispatches. A Planner cannot reach line 1402 through this completion path.

### Planner contract failure (line 1217): overturned

The requested event-log search could not be performed because the goal event log was not included. The bounded copied-context search was:

```text
rg -n -F "Planner output contract failed" .orchestrator-context/707d42446d744ff88e809f7946d57b56 -g '*.md' -g '*.json'
.orchestrator-context/707d42446d744ff88e809f7946d57b56\objective.md:30:   `Planner output contract failed` line appears in `94fdcb8a`'s event log. (It is a real mechanism that
```

That match is the objective restating the prior absence claim, not a runtime receipt. Source establishes that reaching line 1217 appends the diagnostic only to the in-memory completion diagnostic used for `TaskVerificationRecord.StandardError`; it does not append it to the worker stderr file and need not appear in the goal event log. The characterization test positively reaches line 1217 and records the `0`/`1` divergence. The prior event-log absence therefore does not eliminate this site.
