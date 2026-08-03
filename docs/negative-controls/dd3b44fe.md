# Negative-Control Record: dd3b44fe

The subscription Developer was permitted to run only `Invoke-WorkerBuildCheck.ps1`, so the behavioral RED execution is an acceptance-lane obligation. A compile receipt is not a substitute.

Run these scratch mutations one at a time, record the focused test's actual failing assertion, restore the source, and rerun GREEN:

- In `ConductorParallelAcceptanceCandidate.IsDocumentationExcludedFromConflict` in `src/Mcg.AgentOrchestrator.App/Orchestration/ConductorParallelAcceptance.cs`, replace the method body with `return false;`. `ReportedDocumentationIntersection_DoesNotOverlapEitherDirection`, `DocumentationOnlyScope_ReservesNothingAndNeverOverlaps`, `ScopePaths_KeepExcludedDocumentation`, and `DocumentationExclusion_UsesExactPathRule` must go RED. `BatchLoopDocIntersectionAdmitsConcurrentlyWithEvidence` must also go RED because the second candidate is held instead of producing the documentation-exclusion admission record.
- In `ConductorParallelAcceptanceCandidate.Overlaps` in the same file, temporarily replace the method body with `return false;`. `SharedSourcePath_Overlaps` and `SourceDirectoryPrefix_Overlaps` must go RED.

Status: RED receipts deferred to the test-capable acceptance lane; GREEN compile verification is recorded in the goal's worker result.
