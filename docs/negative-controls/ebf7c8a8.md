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
