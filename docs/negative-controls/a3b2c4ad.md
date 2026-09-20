# Post-landing canary collection negative control (a3b2c4ad)

## Mutation

Removed exactly this attribute from `PostLandingCanaryTests.cs`, while leaving the
non-parallel collection definition and reflection fact intact:

```csharp
[Xunit.Collection(TestCollections.PostLandingCanary)]
```

## Managed-runner invocation

```powershell
.\scripts\Invoke-RepoScript.ps1 scripts\Invoke-TestSummary.ps1 -Target tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj -NoBuild -Filter 'FullyQualifiedName~AcceptanceGateEngineSettingsTests&Name~PostLandingCanaryCollectionIsNonParallelAndOwnsCanaryTests'
```

The mutated run retained its red TRX under
`C:\Users\miles\AppData\Local\Temp\Low\mcg-tests\summary-20260919T061635144-a66d8014`.
Result: `total=1 passed=0 failed=1 skipped=0`.

```text
Assert.NotNull() Failure: Value is null
```

The failure was at `PostLandingCanaryCollectionIsNonParallelAndOwnsCanaryTests`,
proving that removing the class membership attribute turns the reflection guard red.

## Restored green control

After restoring the attribute, the identical command completed with exit code 0:
`total=1 passed=1 failed=0 skipped=0`. The managed runner retained the named green
evidence receipt under
`C:\Users\miles\AppData\Local\Temp\Low\mcg-tests\summary-20260919T061852887-e438ee11`.
