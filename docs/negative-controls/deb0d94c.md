# Negative controls: declared file-scope recognition

Goal `deb0d94c` makes file-scope declarations ignore recognized prohibition occurrences,
prune redundant directory ancestors, and preserve independent positive occurrences and genuine
same-file collisions.

## Executed RED/GREEN record

- RED configuration: added
  `GoalFileScopeInferenceTests.ProhibitionClause_PathOnlyOccurrence_IsNotDeclared` while
  production still recognized only `do not touch`, `don't touch`, and `must not touch`.
- RED command: `pwsh -File scripts/Invoke-TestSummary.ps1 -Target
  tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj
  -Filter 'FullyQualifiedName~GoalFileScopeInferenceTests' -NoBuild`.
- RED result: 3 failed, 8 passed. Each receipt row failed with
  `Assert.DoesNotContain() Failure: Filter matched in collection`; the forbidden qualified path
  remained declared with `Provenance = Explicit`. The paired positive arm passed, proving the
  input reached path extraction rather than passing vacuously on its bare filename.
- GREEN configuration: widened the centralized prohibition vocabulary, added trust-aware
  ancestor pruning, and delegated planner scope inference to the same declaration builder.
- GREEN command: `pwsh -File scripts/Invoke-TestSummary.ps1 -Target
  tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj
  -Filter 'FullyQualifiedName~GoalFileScopeInferenceTests|FullyQualifiedName~GoalScopeCollisionAdvisorTests|FullyQualifiedName~GoalRefinementTests'
  -NoBuild`.
- GREEN result: 148 passed, 0 failed, 0 skipped. The selection includes the three receipt rows,
  positive-occurrence precedence, directory pruning/fallback/trust cases, prohibition-only
  insufficient evidence, and the real same-file collision control.

The compile preceding GREEN completed successfully. A prior compile attempt produced `CS2012`
before launching the test host; `dotnet build-server shutdown` cleared the lock, so that attempt
is classified as inconclusive infrastructure evidence rather than RED.
