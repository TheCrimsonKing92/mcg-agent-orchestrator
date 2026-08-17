# Lane timing refined criterion 5 negative control

- Behavior: every process-time and test-time statistic carries the observed min-max range rather than repeating its mean.
- Command: `.\scripts\Invoke-RepoScript.ps1 scripts\Invoke-TestSummary.ps1 -Target tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj -Filter FullyQualifiedName~LaneTimingMeasurementScriptTests -NoBuild`
- RED mutation: replaced `New-Statistics` min and max with the calculated mean.
- RED receipt: 7 executed, 5 passed, 2 failed. `ProcessAndTestStatistics_AreDistinctAndRanged` and `SavedBaseline_ComparisonShowsRangesAndCommits` each reported `Assert.Equal() Failure: Expected: 10, Actual: 15` for Alpha's process minimum.
- GREEN restoration: min and max are selected from the ordered per-receipt observations independently of the mean.
- GREEN commit pointer: `goal/e7a49b08` (the orchestrator-created verified commit containing this receipt).
