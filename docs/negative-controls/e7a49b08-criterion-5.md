# Lane timing criterion 5 negative control

- Behavior: `-IncludeUnqualified` visibly marks every console line and the JSON qualification block as unqualified.
- Command: `.\scripts\Invoke-RepoScript.ps1 scripts\Invoke-TestSummary.ps1 -Target tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj -Filter FullyQualifiedName~LaneTimingMeasurementScriptTests -NoBuild`
- RED mutation: forced `qualification.qualified = true` even when `-IncludeUnqualified` was present.
- RED receipt: 5 executed, 4 passed, 1 failed. `IncludeUnqualified_MarksConsoleAndJson` reported `Assert.All() Failure: 8 out of 8 items` because every line lacked the `UNQUALIFIED ` prefix.
- GREEN restoration: derives the qualification marker directly from the opt-out switch for console and JSON output.
- GREEN commit pointer: `goal/e7a49b08` (the orchestrator-created verified commit containing this receipt).
