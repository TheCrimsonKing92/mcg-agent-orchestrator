# Compile error: `root2` is used after the block that defined it was deleted

Operator review of commit `83fdd878`, 2026-08-20. This will fail the build, so the gate cannot reach the
tests. One line.

## The error

`tests/Mcg.AgentOrchestrator.Infrastructure.Tests/WorkerDispatchTestsWorkerResultClassification.cs:4250`

    Assert.True(root2.TryGetProperty("dispatchState", out var state));

`root2` no longer exists. It was declared inside the block this commit removed:

    var doc = JsonDocument.Parse(line!);
    var root2 = doc.RootElement;

`root2` now appears exactly once in the entire file - that usage - with no declaration anywhere.

## Why it happened, and why the surrounding change is CORRECT

The retention decision for `dispatch-diagnostics.jsonl` was to stop writing it, which the brief explicitly
offered as one of two options given it has no production reader. The replacement assertions are right:

    Assert.False(File.Exists(Path.Combine(logDir, "dispatch-diagnostics.jsonl")));
    Assert.Contains("no production reader", FileDiagnosticWriter.RetentionDecision, StringComparison.Ordinal);

Asserting the file is absent AND that the reason is recorded in code is a better test than the property-bag
checks it replaced. Do not revert that. The only defect is that one surviving line was left behind.

## The fix

Decide what `dispatchState` was verifying and re-express it against a source that still exists, or delete
the line if the diagnostics file was its only source.

Do NOT re-add the JSON parse just to keep the line compiling - that would resurrect the writer this change
deliberately retired.

If `dispatchState` matters independently of the diagnostics file, assert it from the task or verification
record already in scope at that point (`task.Status` and `task.LastVerification` are asserted on the two
preceding lines).

## Check the rest of the same edit

This was a 13-file, 1057-insertion change. Sweep for other references orphaned by removed blocks before
re-dispatching:

    git show 83fdd878 | grep "^-" | grep -v "^---"

and build once locally is not available to you - a live conductor holds the assemblies - so read the
surrounding scope of every deletion instead.
