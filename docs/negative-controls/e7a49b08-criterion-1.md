# Lane timing criterion 1 negative control

- Behavior: identical inputs produce byte-identical console and JSON output.
- Command: `.\scripts\Invoke-RepoScript.ps1 scripts\Invoke-TestSummary.ps1 -Target tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj -Filter FullyQualifiedName~LaneTimingMeasurementScriptTests -NoBuild`
- RED mutation: emitted `Generated at: <UtcNow>` in the primary console table.
- RED receipt: 5 executed, 4 passed, 1 failed. `SameWindow_ProducesByteIdenticalConsoleOutput` failed with `Assert.Equal() Failure: Collections differ` at byte position 58.
- GREEN restoration: removed run-generated wall-clock metadata; the same-window table and JSON remain input-derived and deterministically ordered.
- GREEN commit pointer: `goal/e7a49b08` (the orchestrator-created verified commit containing this receipt).
