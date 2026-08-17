# Lane timing refined criterion 4 negative control

- Behavior: process time and summed TRX test time remain distinct statistics and are never conflated.
- Command: `.\scripts\Invoke-RepoScript.ps1 scripts\Invoke-TestSummary.ps1 -Target tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj -Filter FullyQualifiedName~LaneTimingMeasurementScriptTests -NoBuild`
- RED mutation: built `testSeconds` statistics from each sample's `processSeconds` values.
- RED receipt: 7 executed, 5 passed, 2 failed. `ProcessAndTestStatistics_AreDistinctAndRanged` reported `Assert.Equal() Failure: Expected: 3, Actual: 15`; `SavedBaseline_ComparisonShowsRangesAndCommits` also rejected the conflated test ranges.
- GREEN restoration: `testSeconds` is built only from each sample's summed TRX `testSeconds` value.
- GREEN commit pointer: `goal/e7a49b08` (the orchestrator-created verified commit containing this receipt).
