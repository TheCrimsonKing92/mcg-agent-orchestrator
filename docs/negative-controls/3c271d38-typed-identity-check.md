# Negative control: typed identity-bound hash validation

Goal: `29423867` / backlog slice `3c271d38`
Control: `WorkerContextRendererDispatchPathTests.MandatoryArtifactWithMismatchedStoredHashIsRejectedBeforeRendering`

## Mutation

The RED mutation removed the complete pre-render artifact-hash loop from
`src/Mcg.AgentOrchestrator.Infrastructure/Workers/WorkerContextRenderer.cs`:

```diff
-        foreach (var artifact in package.Artifacts)
-        {
-            ValidateArtifactHash(artifact, workingDirectory);
-        }
```

No test or fixture was changed for the mutation. The control constructs a file-backed mandatory
artifact whose stored `ContentHash` binds `expected bytes`, writes `corrupted bytes` at its declared
materialization path, and invokes the renderer. With the loop removed, rendering continued instead
of rejecting the corrupted identity-bound artifact.

## RED receipt

Command:

```powershell
.\scripts\Invoke-TestSummary.ps1 -Target tests\Mcg.AgentOrchestrator.Infrastructure.Tests\Mcg.AgentOrchestrator.Infrastructure.Tests.csproj -Filter 'FullyQualifiedName~WorkerContextRendererDispatchPathTests'
```

Managed-runner result: 10 total, 9 passed, 1 failed; exit code 2. Retained evidence directory:
`C:\Users\miles\AppData\Local\Temp\Low\mcg-tests\summary-20260918T055707524-9b38dd74`.

Failing assertion:

```text
WorkerContextRendererDispatchPathTests.MandatoryArtifactWithMismatchedStoredHashIsRejectedBeforeRendering
Assert.Throws() Failure: No exception was thrown
Expected: typeof(Mcg.AgentOrchestrator.Core.WorkerContextPreparationException)
```

## GREEN receipt

The mutation was reverted by restoring the loop exactly as shown above. The same managed-runner
command passed 10/10 tests with managed-runner exit code 0. The named TRX/run-identity evidence is
retained at
`C:\Users\miles\AppData\Local\Temp\Low\mcg-tests\summary-20260918T060421620-62f392c8`.

## Stored-format stop-condition check

The stop condition does not trip. `TaskBriefSource` and its budget decisions are per-dispatch,
in-memory values; this change does not alter `artifact-registry.json`, `context-package.json`, the
typed artifact materialization layout, or `TaskDispatchRecord.ContextPackageReceipt`. The parity
theory runs Developer, Tester, and Reviewer fixtures produced by the repository's current
`WorkerContextArtifacts.Write` storage path through both the typed selection and the retained V1
marked-text ingress and requires identical projected identity sets. Existing marked briefs remain
readable through `WorkerContextProjectionResidual.ParseLegacyMarkedTextV1`.
