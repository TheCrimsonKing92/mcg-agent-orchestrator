# Test-Design Discipline

<!-- shared-discipline:test-design-discipline -->

Use this guidance when adding or changing tests, especially tests that feed deterministic gates or exercise subprocesses, repositories, databases, or other shared resources.

a. **Deterministic asserted path.** Assert the branch, state transition, output, or artifact that proves the intended behavior; when crossing a real boundary, assert the precondition that proves that boundary is present and selected before relying on its effects.

b. **Seam every I/O boundary.** Put a controllable seam around git, process, filesystem, network, clock, and database access so tests can force success, failure, delay, and malformed responses.

c. **Narrowest layer.** Test the smallest layer that owns the invariant, and reserve real integration coverage for explicit contract tests with named resources and cleanup.

d. **All-or-nothing gate pinning.** Pin admission gates to precise inputs, outputs, and failure modes so partial success, missing evidence, or fallback behavior cannot pass as green.

e. **Parallel-safety declaration.** Declare whether the test is parallel-safe; tests using shared processes, real repositories, real databases, fixed ports, or global state need isolation or a serial collection.

f. **No silent no-ops.** Fail loudly when a dependency was not invoked, a callback was not observed, zero tests ran or matched, a result set is empty, or a cleanup step did nothing.

Motivating incident: goal `6987f4eb` stabilized the flaky ProgressiveReviewSteering / InquiryDispatcher gate churn tracked by backlogs `3d3dbbf4` and `16ba6136`.

**Future enhancement:** add a lint/analyzer that flags test classes using `GitCli`, `Process.Start`, or a real DB without an explicit serial-collection marker or documented isolation.

## Reviewer Checklist

- Reviewers must check new and changed tests against the `test-design-discipline` guidance before judging residual risk and acceptance readiness.
