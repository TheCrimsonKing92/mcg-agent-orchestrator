# Exit artifact / recorded-code divergence investigation

Goal `707d4244` investigated the two Planner completions from historical goal `94fdcb8a` where the worker-authored exit artifact contained `0` but the recorded verification exit code was `1`.

## Finding

The retained evidence available in this worktree does not discriminate between two surviving assignment sites. The historical assignment is therefore unproven; naming either site as certain would be a guess.

1. `src/Mcg.AgentOrchestrator.Infrastructure/Processes/BackgroundDispatchRunner.cs:1217` is the leading candidate. It writes `exitCode = 1` when `PlannerOutputContract.Resolve(...)` rejects the captured Planner output or returns no plan. Its diagnostic starts with `Planner output contract failed`. The real completion-path test `IncompleteContract_KnownWrongArtifactZeroRecordedCodeOne` proves that a pre-existing artifact containing `0` remains `0` while `TaskVerificationRecord.ExitCode` becomes `1`.
2. `src/Mcg.AgentOrchestrator.Infrastructure/Processes/BackgroundDispatchRunner.cs:1231` remains reachable. It writes `exitCode = 1` after the plan resolves successfully but `PlannerOutputContract.TryPersistDurableReceipt(...)` cannot append the canonical plan receipt to captured stdout. Its diagnostic starts with `Planner output contract could not persist the accepted plan`, so a search only for `Planner output contract failed` cannot eliminate it.

Both writes leave the `orchestratorFailureReason` parameter null. Consequently the restore at lines 1421-1426 does not run. Because `exitArtifactAlreadyExisted` is true, lines 1436-1443 do not overwrite the worker artifact, and line 1450 plus the verification construction at line 1472 record the mutated value.

To discriminate the historical rounds, retain either `TaskVerificationRecord.StandardError` (the two branches produce distinct diagnostics) or an explicit completion event containing the selected contract branch, the observed artifact exit code, the final recorded exit code, and `OrchestratorFailureReason`. The copied context contains neither the goal event log nor the task verification/error artifacts.

No production fix was applied. Choosing whether an orchestrator-authored Planner contract failure should override a clean worker exit is a policy decision; merely setting `orchestratorFailureReason` would cause the existing restore to discard the deliberate contract failure. Classification and label-matching logic were not changed, and the observed frequency labels were not used as causal evidence.

## Follow-up: Planner contract diagnosis (`903cee50`)

The retained Planner stdout for `e5c18520` is available at `.orchestrator/logs/e5c18520-316cfc5a-20260805154436.out.log`: 12,778 bytes, SHA-256 `86A0C693C22296BC7E6517EB7A36384B99057288E057237278F364B3B65844F1`. The test fixture `e5c18520-316cfc5a-20260805154436.out.txt` is byte-identical in its canonical LF form and asserts that hash after undoing any checkout-time line-ending conversion.

The determination is **contract-defect**, not Planner inadequacy. Two retained Planner outputs were substantively complete but rejected on presentation:

1. `e5c18520` explicitly marked new artifacts with descriptive kinds, while `NewFileCitationSuffix` accepted only the exact suffixes `— new file` and `(new file)`. The first two rejected citations were, verbatim:

   > - `src/Mcg.AgentOrchestrator.Infrastructure/Persistence/AcceptanceOwnershipStore.cs` — new SQLite-backed cross-process store. Add active claims, transfer manifests, transactional compare-and-swap adoption, terminal release, and a unique active logical-slot constraint.

   > - `docs/negative-controls/e5c18520.md` — new RED/GREEN receipt.

   Both statements unambiguously describe files to create. The exact-token directive (`(new file)` or `— new file`) landed on 2026-08-08 in `64c81e1d`, after this Planner output was rejected on 2026-08-05; the rejected Planner had only been told to mark new paths explicitly. This correction publishes the accepted descriptive artifact-kind form as well. `ValidateCitedPaths` therefore rejected a well-formed plan at the contract-rejection branch corresponding to `BackgroundDispatchRunner.cs:1217`.

2. `6fd7171d` used a backticked provenance tuple in prose. `NormalizeCitedPath` treated every backticked string containing `/` as a filesystem path, so it rejected `cli / operator / local-process`. The first rejecting citation in the `## Target seams and symbols` span states, verbatim:

   > - the existing CLI-facing wrapper, which always supplies `cli / operator / local-process`;

   Here `cli / operator / local-process` is a value tuple, not a path. The failure was the `citation.Contains('/')` fallback in `NormalizeCitedPath`, again reaching the contract-rejection branch corresponding to line 1217.

The narrow correction admits descriptive new-artifact suffixes only when they end in a recognized artifact kind, and ignores slash-delimited prose only when whitespace surrounds the separators. Negative controls retain rejection for a missing path followed merely by `new behavior`. The complete `e5c18520` artifact and the retained `6fd7171d` artifact both resolve after the correction.

The earlier marker-regex defect is not recurring: the rejected artifacts use numbered acceptance mappings that satisfy `TryValidateCriterionMappings`; neither depends on `\b(?:map|maps|mapped|mapping)\b` in a section body. This incident instead exposed two independent presentation predicates in path validation.

Stored dispatch artifacts were inspected for all four named goals without querying live orchestrator state:

| Goal | Retained-artifact result | Site attribution |
|---|---|---|
| `94fdcb8a` | One Planner artifact rejects the existing basename citation `CliCommandTests.AttentionCommands.cs`; other retained Planner artifacts resolve. | Unattributable to a recorded non-zero round without the persisted `TaskVerificationRecord.StandardError`; stdout filenames alone do not establish which verification record is in scope. |
| `6fd7171d` | The retained `20260808142417` Planner artifact rejects slash-delimited provenance under the pre-fix predicate and resolves after the correction. | Contract rejection / no plan, line 1217, for that artifact. |
| `e5c18520` | The supplied rejected `20260805154436` Planner artifact fails the two descriptive new-artifact markers above and resolves after the correction. | Contract rejection / no plan, line 1217. |
| `6a960b3f` | The retained `20260808132803` Planner artifact rejects the root-relative basename `DispatchOutcomeClassifyTests.cs`; the later `20260808133641` artifact has a durable ingested receipt. | The first artifact reaches line 1217; the persisted non-zero record cannot be independently matched without `TaskVerificationRecord.StandardError`. |

Unaddressed follow-up: root-relative basename citations remain a separate presentation-defect shape and account for two of the four observed goals. Retained artifacts `.orchestrator/logs/94fdcb8a-1ca3f5f9-20260808122725.out.log` (`CliCommandTests.AttentionCommands.cs`) and `.orchestrator/logs/6a960b3f-9f908e37-20260808132803.out.log` (`DispatchOutcomeClassifyTests.cs`) preserve the evidence. This slice does not widen path inference to guess directories for bare basenames.

The two line-1217/line-1231 labels and the exit-artifact policy remain out of scope here and owned by backlog `ba3e1bde`. The current task explicitly prohibited orchestrator-state access, so persisted `TaskVerificationRecord.StandardError` values were not queried; rows above say unattributable where stored files cannot substitute for that record.

The model correlation is retired. The retained dispatch metadata selected `codex exec --model 'gpt-5.6-sol'` with `model_reasoning_effort='high'`, but `AgentCatalogStore.Default()` binds the Planner to subscription model `gpt-5.5` at low effort, allows alternate catalogs, and applies complexity selection separately. The failing model/effort pair is therefore not a static property of the Planner role and carries no discriminating signal for this contract defect.

Verification state: the characterization test asserts the current divergent behavior (it is not skipped). The subscription lane's sanctioned `Invoke-WorkerBuildCheck.ps1` completed with zero errors, and the focused `PlannerOutputContractTests` run passed all 18 tests, including the real-artifact acceptance test and fixture-derived negative control.

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

Implementation inventory for goal `06409f18`: seven of these sites emit dedicated machine-readable diagnostic codes (1187, 1197, 1217, 1231, 1280, 1376, and 1402). Site 1341 is deliberately omitted. Every path out of its dirty-worktree branch appends a diagnostic recognized by the pre-existing `dirty-dispatch-recovery` rule, so that named recovery outcome returns before the authored-diagnostic fallback can run. The positive Infrastructure test for a dirty completion asserts that receipt and the unchanged failed disposition; adding an eighth marker there would be inert and would misstate the classifier surface.

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
