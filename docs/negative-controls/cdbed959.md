# Planner candidate selection negative controls

Scope: conductor Planner-worker sampling only. No paid worker or live N=2 evaluation was started.

## Structural N=2 selection

- Mutation/RED state: production selector used symmetric pairwise Jaccard scoring for exactly two valid candidates, so index order decided the tie.
- Command: `.\scripts\Invoke-RepoScript.ps1 scripts\Invoke-TestSummary.ps1 -Target .\tests\Mcg.AgentOrchestrator.Infrastructure.Tests\Mcg.AgentOrchestrator.Infrastructure.Tests.csproj -Filter 'DisplayName~TwoValidCandidates_StructuralEvidence_SelectsStrongerPlan'`
- RED receipt: one test executed; `Assert.Equal() Failure: Values differ`, expected selected candidate `1`, actual `0`.
- GREEN behavior: exactly two different valid candidates use the bounded structural vector; irrelevant prose does not score, identical candidates collapse explicitly, and a structural tie records primary fallback.

## Acceptance-owned remaining controls

The focused suite contains timeout, successful-exit empty, invalid-primary/valid-secondary, primary-valid/invalid-secondary, identical, two-valid-different, and all-invalid cases. Acceptance must perform the requested one-behavior-at-a-time mutations for normalization, terminal typing, and receipt persistence, retain each predicted RED message, restore production, and retain final GREEN before authorizing any paired corpus run.
