# Lane timing criterion 3 negative control

- Behavior: every qualification exclusion is tallied by its fixed reason.
- Command: `.\scripts\Invoke-RepoScript.ps1 scripts\Invoke-TestSummary.ps1 -Target tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj -Filter FullyQualifiedName~LaneTimingMeasurementScriptTests -NoBuild`
- RED mutation: made `Add-Exclusion` a no-op while leaving all receipt fixtures unchanged.
- RED receipt: 5 executed, 3 passed, 2 failed. `ExcludedReceipts_AreTalliedByReason` failed because output omitted `Receipts: total=9 included=4 excluded=5`; the unqualified-count assertion also failed because it consumes the same exclusion tally.
- GREEN restoration: restored one ordered reason increment per excluded receipt.
- GREEN commit pointer: `goal/e7a49b08` (the orchestrator-created verified commit containing this receipt).
