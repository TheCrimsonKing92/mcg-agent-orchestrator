# Lane timing criterion 4 negative control

- Behavior: saved measurement JSON is interchangeable with `-Json` output and `-CompareTo` emits both ranges, deltas, qualification counts, and commit SHAs.
- Command: `.\scripts\Invoke-RepoScript.ps1 scripts\Invoke-TestSummary.ps1 -Target tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj -Filter FullyQualifiedName~LaneTimingMeasurementScriptTests -NoBuild`
- RED mutation: suppressed deserialized baseline-lane lookup while leaving the freshly measured lanes intact.
- RED receipt: 5 executed, 4 passed, 1 failed. `SavedBaseline_ComparisonShowsRangesAndCommits` reported `Assert.Contains() Failure: Sub-string not found` for `Alpha | present | 10-20 | 15-25 | 5 | 2-4 | 3-5 | 1`.
- GREEN restoration: restored dictionary/JSON-object lane lookup through the same accessor.
- GREEN commit pointer: `goal/e7a49b08` (the orchestrator-created verified commit containing this receipt).
