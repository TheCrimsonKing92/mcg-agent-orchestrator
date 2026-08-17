# Lane timing criterion 2 negative control

- Behavior: a manifest lane without qualified data fails non-zero and names the missing lane.
- Command: `.\scripts\Invoke-RepoScript.ps1 scripts\Invoke-TestSummary.ps1 -Target tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj -Filter FullyQualifiedName~LaneTimingMeasurementScriptTests -NoBuild`
- RED mutation: preserved the non-zero failure but replaced the missing lane list with `<suppressed-for-negative-control>`.
- RED receipt: 5 executed, 4 passed, 1 failed. `MissingManifestLane_FailsAndNamesLane` reported `Assert.Contains() Failure: Sub-string not found`; stderr omitted `Gamma`.
- GREEN restoration: restored the ordinally sorted missing-lane list in stderr before any shortened table can be emitted.
- GREEN commit pointer: `goal/e7a49b08` (the orchestrator-created verified commit containing this receipt).
