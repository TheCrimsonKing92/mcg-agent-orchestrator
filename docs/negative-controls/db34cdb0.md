# Negative control: TRX coherence counts only verdict rows, so a skipped opt-in row stays green

Goal `db34cdb0` changes `AcceptanceCohortGateEvidence.HasCoherentExecutedTrxEvidence` in
`src/Mcg.AgentOrchestrator.Core/Domain/AcceptanceCohorts.cs` so the row population is reconciled
against the counters by verdict: rows whose outcome is `Passed` or `Failed` must equal the
`executed` counter, and rows whose outcome is `NotExecuted` must equal `total - executed`. It
replaces the previous `unitResults.Length == executed` check, which counted a skipped opt-in row as
executed and turned an all-green lane into `InfrastructureFailure` with reason
`trx-evidence-incoherent`.

This receipt proves the new fact
`ConductorAcceptanceCohortTests.SkippedRowWithMatchingCounters_IsPassed` fails when the
`NotExecuted` exclusion is reverted, so the fact is bound to the predicate change and not merely
asserting on its own fixture.

## Mutation

In `src/Mcg.AgentOrchestrator.Core/Domain/AcceptanceCohorts.cs`, inside
`HasCoherentExecutedTrxEvidence`, the two new row conditions

```csharp
            passedResults + failedResults == executed &&
            notExecutedResults == total - executed &&
```

were replaced by the pre-change condition

```csharp
            unitResults.Length == executed &&
```

Nothing else changed in either arm: the `notExecutedResults` tally above the `return` stayed in
place (unused under the mutation), every retained condition (`total > 0`, `executed > 0`,
`executed <= total`, `passedResults == passed`, `failedResults == failed`,
`passed + failed == executed`) was untouched, and no test file was edited between arms.

## Invocation

Both arms ran the same managed-runner command from the goal worktree
`C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\db34cdb0`:

```powershell
.\scripts\Invoke-TestSummary.ps1 -Target .\tests\Mcg.AgentOrchestrator.Infrastructure.Tests\Mcg.AgentOrchestrator.Infrastructure.Tests.csproj -Filter "FullyQualifiedName~ConductorAcceptanceCohortTests"
```

The mutation is scoped to two lines of one predicate; the run is scoped to the whole
`ConductorAcceptanceCohortTests` class, so the other twenty-three cases — including the four
`NonVerdictTrxOutcome_IsInfrastructureFailure` theory cases and `PassingGateRequiresParseableTrxEvidence` —
act as the control group inside the same run.

## RED (mutation applied)

```
failed ConductorAcceptanceCohortTests.SkippedRowWithMatchingCounters_IsPassed (2ms)
  Assert.Equal() Failure: Values differ
  Expected: Passed
  Actual:   InfrastructureFailure
    at ConductorAcceptanceCohortTests.SkippedRowWithMatchingCounters_IsPassed() in C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\db34cdb0\tests\Mcg.AgentOrchestrator.Infrastructure.Tests\ConductorAcceptanceCohortTests.cs:285
```

```
Test run summary: Failed!
  total: 24
  failed: 1
  succeeded: 23
  skipped: 0
  duration: 1s 290ms
```

Exactly one case moved. The failure is the classification itself — `InfrastructureFailure` where
`Passed` is required — which is the operator-visible symptom the goal removes, not a fixture or
parse error.

The companion fact `SkippedRowCountedAsExecuted_IsInfrastructureFailure` passes in both arms by
design: a TRX with one `Passed` row, one `NotExecuted` row and counters
`total="2" executed="2" passed="1" failed="0"` has one verdict row against an `executed` counter of
two, so it is incoherent before and after the change. It is a guard against over-widening the
predicate, not a mutation-sensitive fact, and its RED-arm pass is therefore the expected result.

## GREEN (mutation reverted)

```
Test run summary: Passed!
  total: 24
  failed: 0
  succeeded: 24
  skipped: 0
  duration: 524ms
```

The two arms ran different configurations of the same source tree — the RED arm with
`unitResults.Length == executed`, the GREEN arm with the verdict-row and `NotExecuted`
reconciliation restored — and each arm rebuilt the test assembly from source before executing
(`Build succeeded. 0 Error(s)` in both), so the differing verdicts come from the mutation rather
than from a stale binary or a cached run receipt. The GREEN arm also confirms the four existing
`NonVerdictTrxOutcome_IsInfrastructureFailure` theory cases (`Error`, `Aborted`, `Timeout`,
`NotExecuted`) stay red-for-the-right-reason: each has zero verdict rows against `executed="1"`, so
the predicate still rejects a TRX whose executed counter exceeds its verdict rows.
