# Planner output-contract residuals negative control

Date: 2026-08-04

The negative-control mutation disabled only the new ordered-list, post-path new-file-marker, contextual-sibling, and Planner-contract classification guards. No `BackgroundDispatchRunner` behavior was changed.

## RED

- Command: `Mcg.AgentOrchestrator.Infrastructure.Tests.exe --filter-class PlannerOutputContractTests --minimum-expected-tests 6 --no-ansi --progress off --output Normal`
- Result: exit 1; 6 ran, 4 failed, 2 passed. The four live-artifact positives failed respectively on the missing integration-sequence keyword, em-dash marker, parenthesized marker, and contextual sibling.
- Command: `Mcg.AgentOrchestrator.Core.Tests.exe --filter-method '*ClassifyPlannerOutputContractFailure' --minimum-expected-tests 1 --no-ansi --progress off --output Normal`
- Result: exit 1; expected `UnknownFailure`, actual `ProviderModelRejection`.

## GREEN

After restoring the production guards:

- The same Planner contract command passed 6/6, including malformed-list and hallucinated-sibling controls.
- The classification command, paired with the genuine provider-model-rejection control, passed 2/2.

## Retry 5 residual controls

The final residual test set adds the byte-identical `485363d4-ba8e416a-20260805022806.out.log`
plan as a tracked `.out.txt` fixture, explicit malformed numbering, same-line existing-sibling success,
newline-reset hallucinated-sibling failure, an independent provider diagnostic line, and the two
history/count consumers. The subscription Developer policy permits only `Invoke-WorkerBuildCheck.ps1`
and forbids worker-side test-host execution, so this round has no new behavioral RED/GREEN execution
receipt. The test-capable acceptance lane must retain a focused RED receipt with the corresponding
ordered-list/context reset/authoritative-prefix production branches disabled, restore them, and run
`DispatchOutcomeClassifyTests`, `PlannerOutputContractTests`, and `WorkerDispatchTests` GREEN at the
final commit. The compile receipt is not a substitute for rule (l).

## Retry 9 final residual controls (2026-08-05)

The temporary mutation made provider-model detection ignore stdout and restored the historical
same-basename fallback while a same-line directory context was active. The mutation build passed
with 0 errors, proving the RED results were behavioral rather than compilation failures.

### RED

- `Invoke-TestSummary.ps1 -Target tests/Mcg.AgentOrchestrator.Core.Tests/Mcg.AgentOrchestrator.Core.Tests.csproj -Filter 'FullyQualifiedName~DispatchOutcomeClassifyTests' -NoBuild -RunnerPath C:\Users\miles\AppData\LocalLow\mcg-dotnet-isolated\goals\ebf7c8a8\artifacts\bin\Mcg.AgentOrchestrator.Core.Tests\debug\Mcg.AgentOrchestrator.Core.Tests.exe`: exit 2; 75 ran, 72 passed, 3 failed. The cross-stream regression expected `ProviderModelRejection` but got `UnknownFailure`; both provider-rejection history consumers also failed.
- `Invoke-TestSummary.ps1 -Target tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj -Filter 'FullyQualifiedName~PlannerOutputContractTests' -NoBuild -RunnerPath C:\Users\miles\AppData\LocalLow\mcg-dotnet-isolated\goals\ebf7c8a8\artifacts\bin\Mcg.AgentOrchestrator.Infrastructure.Tests\debug\Mcg.AgentOrchestrator.Infrastructure.Tests.exe`: exit 2; 12 ran, 11 passed, 1 failed. `PlannerContract_ActiveContextDoesNotUseHistoricalSameBasenameFallback` expected failure but the mutated resolver incorrectly succeeded.

### GREEN

After restoring both production guards:

- `Invoke-WorkerBuildCheck.ps1` for Core, Infrastructure, Core.Tests, and Infrastructure.Tests: 0 errors.
- The same Core classifier command: 75/75 passed.
- `Invoke-TestSummary.ps1` for `PlannerOutputContractTests`, `FailureTriageDecisionTests`, and the two RunGoal provider-rejection/Planner-contract methods: 18/18 passed.
- `Invoke-TestSummary.ps1 -Filter 'FullyQualifiedName~WorkerDispatchPlannerHandoffTests'`: 21/21 passed, retaining dispatch-completion and durable-receipt coverage without changing `BackgroundDispatchRunner`.
