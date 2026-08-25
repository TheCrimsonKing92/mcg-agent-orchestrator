# Planner candidate selection negative controls

Scope: conductor Planner-worker sampling only. No paid worker or live N=2 evaluation was started.

## Structural N=2 selection

- Mutation/RED state: production selector used symmetric pairwise Jaccard scoring for exactly two valid candidates, so index order decided the tie.
- Command: `.\scripts\Invoke-RepoScript.ps1 scripts\Invoke-TestSummary.ps1 -Target .\tests\Mcg.AgentOrchestrator.Infrastructure.Tests\Mcg.AgentOrchestrator.Infrastructure.Tests.csproj -Filter 'DisplayName~TwoValidCandidates_StructuralEvidence_SelectsStrongerPlan'`
- RED receipt: one test executed; `Assert.Equal() Failure: Values differ`, expected selected candidate `1`, actual `0`.
- GREEN behavior: exactly two different valid candidates use the bounded structural vector; irrelevant prose does not score, identical candidates collapse explicitly, and a structural tie records primary fallback.

## Normalization, terminal typing, and receipt persistence

Acceptance executed each mutation independently on 2026-08-25 using the isolated no-paid-worker apphost, then restored production before the next control.

- Normalization mutation: changed the recognized Codex envelope result from `Normalized` to `NotRequired`. `CollectCandidates_CodexJsonl_NormalizesWithoutProcessStart` ran 1 test and failed with `Expected: Normalized; Actual: NotRequired`.
- Terminal-typing mutation: omitted the typed timeout terminal record while retaining the human diagnostic. `UnresolvedSampleTimesOutAndPrimarySucceeds` ran 1 test and failed with `Expected: TimedOut; Actual: LaunchFailed`.
- Receipt-persistence mutation: omitted `selection.Receipt` from the verification snapshot. `LateFinishingSampleStillReachesSelector` ran 1 test and failed with `Assert.IsType() Failure: Value is null; Expected: PlannerCandidateDivergenceReceipt; Actual: null`.
- Restored GREEN command: `Invoke-TestSummary.ps1` against the isolated current apphost with `FullyQualifiedName~PlannerCandidateDivergencePersistenceTests|FullyQualifiedName~PlannerCandidateSelectorTests|FullyQualifiedName~PlannerSamplingDispatchTests|FullyQualifiedName~WorkerContextUsageReceiptTests`; 47 tests passed, 0 failed after all three production restorations.

The focused suite also contains successful-exit empty, invalid-primary/valid-secondary, primary-valid/invalid-secondary, identical, two-valid-different, and all-invalid controls. No paid worker or live N=2 evaluation was started.
