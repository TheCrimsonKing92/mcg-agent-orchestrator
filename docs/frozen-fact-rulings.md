# Frozen-fact rulings

On 2026-10-02 at 20:05Z the owner kept requests to amend a test fact frozen by a brief
criterion with the operator. The operator logs every ruling. After about five rulings
following one procedure, the Author may receive that narrow rule with a Reviewer check
that the edit stayed inside it. Six rulings now support this hand-off. The Author may
issue only a ruling fully supported by the procedure; every other case stays an owner
question. The existing owner-class check still applies.

## Four-step procedure

0. Every allowed change must be a literal edit the Developer can make without running tests.
1. Confirm the fact builds exactly the behavior the goal's objective deliberately removes or moves.
2. Allow changing only the assertions or path pieces that pin that behavior. An allowed change may also edit fixture inputs (literals, arguments or setup lines) that feed the pinned behavior, provided every assertion stays byte-identical.
3. Name every other assertion in that fact, and every other fact in the frozen classes, as unmodified.
4. Give the Developer a diff check it can run on its own output.

Removing an assertion is never an allowed change. The goal brief and current source
must support all four steps. Otherwise the Author asks the owner, or refuses in an
answer that points to the owner. Deterministic validation checks the structured fields,
class membership, files and source/test evidence before the unchanged owner-class check.
Only human-input targets can receive rulings; collaboration Clarifications go to the owner.

## Shared text format

`FrozenFactRuling.Render` emits the following plain text. Repeat `Amended fact:` for
each amended fact. Lists use comma-separated values. Newlines inside a value collapse
to spaces. The first non-blank line must be exactly the header; free-text answers do
not become rulings. Operators can submit this same format as their answer.

```text
Frozen-fact ruling v1
Frozen classes: ClassA, ClassB
Amended fact: ClassA.Method in tests/Project/ClassA.cs: the only allowed change
Basis: why this fact builds exactly the deliberately removed or moved behavior
Unmodified: every other assertion in the amended fact and every other fact in ClassA and ClassB
Diff check: the command and decision procedure to check the Developer's own diff
Evidence: tests/Project/ClassA.cs:42, src/Project/Behavior.cs:17
```

Every later task brief in the goal includes answered, active spec-clarification-class
rulings in full, with request ids and Developer or Reviewer instructions. Whole rulings
are retained oldest first up to 6,000 rendered characters; a line names every omitted
request id. Dismissed and superseded answers are excluded.

## Operator exemplars

These are historical rulings, including the precise attention-block replacement in
the first receipt. They do not authorize future assertion removals.

| Ruling / date / goal | Pinned behavior and permitted edit | Unmodified scope and diff check |
| --- | --- | --- |
| 1 / 2026-10-02 / beba848c | `CliCommandTestsTerminalSweepCommands.TerminalGoalSweepNextAndConductSurfaceSameUnmergedBranchBlocker`, `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/CliCommandTests.TerminalSweepCommands.cs:965-973`, builds the Verified, unmerged, no-gate-receipt shape suppressed by the goal; only its attention block (`Assert.Single(attention)` and four item assertions) became `Assert.Empty(attention)`. | All SWEEP_BLOCKER assertions on next, conduct and diagnostics and the final `GoalWorktrees.TryResolve` NotNull check stayed; every other fact in `CliCommandTestsTerminalSweepCommands`, `ReconcileSweepPendingGateEscalationTests` and `ReconcileSweepRemediationTests` stayed. Check `git diff` of that file touches only the attention block in the named fact. |
| 2 / 2026-10-03 / 6552eebc | `NegativeControlPromptRuleTests` pins the exact `NegativeControlRequestRule` line rewritten by the goal; only its `proof` literal changed to the new rule text. | Its assertions and all other facts stayed; check the diff changes only that literal. |
| 3 / 2026-10-03 / 5043c7fa | `LoopHealthReportTests.LoopHealthFalseBlockRateCountsConsensusNotMetForCompletedGoals` pins the false-block scoring removed by the owner-approved change; only FalseBlockRate changed from 0.5 to 0.0 and `RejectedThenLandedCount == 1` was added. | Everything else stayed frozen; check the diff changes only those assertions in that fact. |
| 4 / 2026-10-03 / 28ea80c3 | `ProcessTreeGuiSuppressionTests.WorkerAndGateRootLaunchesUseProcessTreeGuiSuppression` and `WorkerProcessJobsTests.AssertProductionCallersObserveRegistrationFailure` read the moved code in `GoalAcceptanceVerifier.cs`; only the file name in each path/key changed to `GoalAcceptanceVerifier.ProcessRunner.cs`. | Every searched string, ordering assertion and other fact stayed byte-identical; check only those two file-name pieces differ. |
| 5 / 2026-10-04 / c29815e4 | `OrchestratorTempRootSourceGuardTests.ProductionSourceUsesSharedTempRootOrDocumentedException` pins `Path.GetTempPath(),` to `DotnetBuildEnvironmentManager.cs`; only that allowed row's path changed to `Processes/DotnetBuildStorageLayout.cs` as the code moved. | Snippet, count 1, reason, all other rows and facts stayed byte-identical; check the diff changes only that row's path. |
| 6 / 2026-10-04 / 53574582 | `WorkerProcessJobsTests.AssertProductionCallersObserveRegistrationFailure` pins a `checkedCallers` key to `Processes/LocalProcessVerifier.cs`; only its folder piece changed to Workspaces with the moved source. | Expected call string, other entries, every assertion and other fact stayed byte-identical; check the diff changes only the folder piece of that key. |
| 7a / 5330f7f6 | Worker termination recording moves to the in-memory fixture: rename `RunLegacy` to `RunInMemory` and record its terminations in a separate list. | Every assertion stays byte-identical; `git diff main -U0` on the affected test files must show only the rename and separate termination recording. |
| 8 / 5330f7f6 | The goal stops terminating workers on park and abandon; two `Assert.False(task.LastProcess!.IsRunning)` lines flip to `Assert.True`. | Every other assertion and fact stays untouched; `git diff main -U0` must show one changed assertion line per file and no other changes. |
| 9 / 7b911976 | Two live-repository classifier facts are removed as declared test removals; the cache fact becomes conditional only on a signalled bound degradation. | All other facts and cache checks remain unchanged; `git diff main -U0` must show only the two declared removals and the cache condition on signalled bound degradation. |
| 10 / c64b0239 | Facts pin paths of moved files; allow project-segment path rewrites and list additions for those moved files. | Every assertion stays byte-identical; `git diff main -U0` must show only the moved-file project segments and listed additions. |
| 10a / c64b0239 | Additional moved-file paths need project-segment rewrites and list additions under the same move contract. | Every assertion stays byte-identical; `git diff main -U0` must show only the additional moved-file project segments and listed additions. |
| 11 / c0839d66 | Label-only path literals pin the old project location; allow project-segment rewrites and remove one step-1 fact by declared test removal. | All assertions outside the declared removed fact and all other facts stay unchanged; `git diff main -U0` must show only the permitted path segments and the declared fact removal. |
| 11a / c0839d66 | `ConductorBatchLoopTestsSelfHandoffDrainCap.DispatchHandlingLandingKeepsFullDrainAtCap` changes the `BackgroundDispatchRunner.cs` RunScenario project segment from Infrastructure to Execution; `RepositoryChangeClassifierDispatchHandlingPathsTests.DispatchAndResultHandlingChangesKeepTheFullDrain` makes the same rewrite in the backslash `BackgroundDispatchContracts.cs` InlineData literal, adding to ruling 11. | Every assertion, other fact and other literal outside ruling 11 stays byte-identical, including the negative `BackgroundDispatchNotes.md` case. `git diff main -U0 -- tests/Mcg.AgentOrchestrator.Infrastructure.Tests/ConductorBatchLoopTestsSelfHandoffDrainCap.cs` must show exactly one changed path line; `git diff main -U0 -- tests/Mcg.AgentOrchestrator.Core.Tests/RepositoryChangeClassifierDispatchHandlingPathsTests.cs` must show only the permitted project-segment rewrites, preserving separators and remainders, with no line additions or removals. |
| 12 / ed2bf978 | The facts pin the former Developer lines; one requirement literal and two array elements per variant change to the new Developer lines. | All other literals, array elements, assertions and facts stay untouched; `git diff main -U0` must show only that requirement literal and those two elements per variant. |
| 13 / 14f2e9b8 | Output gains the new `next=` field; an anchored regex tail and one exact equality become a prefix match ending in that field. | Every other assertion and fact stays unchanged; `git diff main -U0` must show only the regex tail and equality-to-prefix change for `next=`. |
| 14 / 64b8636e | Replay fixture routing changes: four calls gain the fixture argument `skipReadOnlyRoute: true`. | Every assertion stays byte-identical; `git diff main -U0` must show only that argument addition in the four replay calls. |
| 16 / 107b7e93 | Five rationale-less WORKER_RESULT fixture rows feed the changed file-report behavior; change `files: none` to `files: Alpha.cs`. | Every assertion and every other fixture field stays byte-identical; `git diff main -U0` must show only the five listed `files` fixture-input substitutions. |
| 17 / 7d406294 | The fact needs shared git metadata: move setup from a standalone repository to `CreateLinkedWorktree` and make the fake labeler report Medium for shared git metadata. | Every assertion stays byte-identical; `git diff main -U0` must show only the linked-worktree setup and the fake labeler's per-path state for shared git metadata. |
