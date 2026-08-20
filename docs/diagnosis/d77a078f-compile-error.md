# The candidate does not compile. One shadowed local, in production code.

Two focused-evidence runs failed at 19:01 and 19:05, both `exit 1` with `tests_executed: unknown` and a
duration of 23 seconds. No tests ran at all. The build phase failed in 11.6 seconds with 6 errors.

## The error

    src/Mcg.AgentOrchestrator.Infrastructure/Workers/PlannerOutputContract.cs(614,29):
    error CS0136: A local or parameter named 'citationStart' cannot be declared in this scope
    because that name is used in an enclosing local scope to define a local or parameter

The other five errors are `MSB4181` cascades in App, Infrastructure.Cli.Tests, TestSupport, and
Dashboard.Tests. They are downstream of this one and will disappear when it is fixed.

`citationStart` already exists in the enclosing scope, where the original "does not exist" diagnostic is
built just before the fatal `return false`. The new code at line 614 declares it again. Rename the new one,
or reuse the existing value if it is the same span.

## Do not go looking elsewhere

The Reviewer round reported its blocker at `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/PlannerOutputContractTests.cs:4`.
That location is misleading. The test file is fine. The defect is in the production file above.

## Why this note exists

A worker that reports failing verification currently has its dispatch discarded by the recognizer in
`BackgroundDispatchRunner.HasCompletedVerification`, which treats "found failing tests" as "did not complete
verification". That is the sibling defect being fixed under backlog `0699a50d` and goal `602e109f`. If the
Tester round that found this compile failure is discarded, the finding would be lost, so it is recorded here
on the branch where it cannot be thrown away.

## Everything else stands

The plan and the substance of the change are not in question. This is a one-line scoping fix, not a redesign.
Do not change the citation grammar decisions, do not weaken any test, and do not re-derive the analysis.
