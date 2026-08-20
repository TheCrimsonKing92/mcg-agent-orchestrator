# Correction: the timing-out test is in MtpTestRunnerScriptTests, not the ConductorSelfRelaunch tests

My earlier note `602e109f-gate-timeout-flake.md` said I could not isolate which test in the Process spawning
lane timed out, and listed three `ConductorSelfRelaunch_real_*` tests as candidates. That guess was wrong.
Ignore those names.

## The actual test

`tests/Mcg.AgentOrchestrator.Infrastructure.Tests/MtpTestRunnerScriptTests.cs` around line 108 runs a spawned
process with:

    var result = Run(startInfo, timeout: TimeSpan.FromMinutes(5));

`TimeSpan.FromMinutes(5)` is exactly the 300 seconds named in the failure, `System.TimeoutException : dotnet
did not exit within 300 seconds`. That is the test.

This was confirmed independently: goal `ae9dccd4` hit the same failure in the same lane an hour earlier, and
its Developer located and fixed this exact line by widening the budget to 15 minutes, leaving the assertion
`Assert.True(result.ExitCode == 0, ...)` untouched.

## What this means for you

Still nothing to do in this goal. The conclusion is unchanged and is now better supported: the failure is a
wall-clock budget being missed under host load, in a test that has nothing to do with dispatch-result
recognition.

Do not make the same edit here. `ae9dccd4` already carries it and is ahead of you in the pipeline. Two goals
editing the same line in the same test file would collide at landing, which is exactly the file-scope
conflict the briefs are written to avoid.

Report your round as complete with no changes if you have nothing else outstanding.
