# Test-Design Discipline

<!-- shared-discipline:test-design-discipline -->

Use this guidance when adding or changing tests, especially tests that feed deterministic gates or exercise subprocesses, repositories, databases, or other shared resources.

a. **Deterministic asserted path.** Assert the branch, state transition, output, or artifact that proves the intended behavior; when crossing a real boundary, assert the precondition that proves that boundary is present and selected before relying on its effects.

b. **Seam every I/O boundary.** Put a controllable seam around git, process, filesystem, network, clock, and database access so tests can force success, failure, delay, and malformed responses.

c. **Narrowest layer.** Test the smallest layer that owns the invariant, and reserve real integration coverage for explicit contract tests with named resources and cleanup.

d. **All-or-nothing gate pinning.** Pin admission gates to precise inputs, outputs, and failure modes so partial success, missing evidence, or fallback behavior cannot pass as green.

e. **Parallel-safety declaration.** Declare whether the test is parallel-safe; tests using shared processes, real repositories, real databases, fixed ports, or global state need isolation or a serial collection.

f. **No silent no-ops.** Fail loudly when a dependency was not invoked, a callback was not observed, zero tests ran or matched, a result set is empty, or a cleanup step did nothing.

g. **Never assert on real wall-clock time.** Elapsed real time is a property of the machine, not of the code under test, so an assertion on it encodes "the box was idle" as a correctness condition. Take time from an injected clock/`TimeProvider` and assert the decision the code made (timed-out vs completed, the recorded duration it reported), never `stopwatch.Elapsed < N`. Synchronize on signals — `TaskCompletionSource`, an awaited event, a seam callback — never on `Sleep`/`Delay`. **Timeouts are failsafes, never assertions:** a wait bound exists so a broken test fails instead of hanging, and it must be generous enough that only a genuine hang trips it; if shortening it would change the verdict, the test is asserting on timing and is defective.

h. **Reach the OS only through an injected seam — and never assert on machine-global observations.** Process spawn, kill, and enumeration go through an injectable process host, as with git/filesystem/network/clock/database in (b). Beyond that: a test's verdict must never depend on what else exists on the machine. Whole-machine scans (`Process.GetProcesses()`), the OS PID namespace (PIDs are recycled and shared), user-session services (`dotnet build-server shutdown` tears down MSBuild/Roslyn for every concurrent build), and shared temp roots are all observations no scheduling can make correct — an unrelated process, another lane, or the operator's own conduct loop can satisfy or break them. Scope every such observation to the test's own root, registry, or handle set.

i. **Isolation is the fix; scheduling is not.** If a test can fail because the machine is busy or because something else ran concurrently, it is defective *as a test* — serializing it against other work hides the defect and buys a permanent throughput tax. Fix the test to be independent (seam the resource, scope the observation, inject the clock). Reserve declared exclusive resources for genuinely irreducible session-wide state, name that resource honestly rather than by the xUnit collection that happens to hold it, and treat each one as a standing cost to justify.

Motivating incidents: goal `6987f4eb` stabilized the flaky ProgressiveReviewSteering / InquiryDispatcher gate churn tracked by backlogs `3d3dbbf4` and `16ba6136`. Goal `f2cdd6d7` then hit the class that (g)–(i) exist to prevent: a 3,460-test acceptance run failed 4 tests that each passed individually, because they asserted wall-clock budgets and machine-global process state that four-wide concurrent lanes could not honor.

**Enforcement (the point of this section — prevention, not cleanup):** these rules only hold if they are checked mechanically. Add a lint/analyzer that fails the build on: `Process.Start`/`Process.Kill`/`Process.GetProcesses`/`Process.GetProcessById` used directly in test code, `Thread.Sleep`/`Task.Delay` used as synchronization, assertions over `Stopwatch`/`DateTime.Now`/`DateTimeOffset.UtcNow`, and test classes touching `GitCli` or a real DB without an explicit serial-collection marker or documented isolation. Until that analyzer exists, the Reviewer checklist below is the enforcement.

## Reviewer Checklist

- Reviewers must check new and changed tests against the `test-design-discipline` guidance before judging residual risk and acceptance readiness.
