# The Theory arms pass standalone and fail only inside the gate

> **CORRECTION, appended after the round-7 review.** The claim below that the failure is
> undiagnosable from the record is **wrong**, and so is the request to capture output that is
> already captured. The TRX for attempt `8ab0a29d-0-20260816120002525` contains 22 occurrences of
> `MSB3021`. The operator missed them by extracting `<Message>` with a 400-character bound, which
> truncated the build log, and then concluded from that truncation that the detail was absent.
>
> The real cause, per review finding `ONESTEP-CONTRACT-MAXPATH-GATE-25` and confirmed in the same
> TRX:
>
> - `MSB3021 ... exceeds the OS max path limit` on the Cli and ProviderEnvironment arms, copying
>   `sqlitepclraw.lib.e_sqlite3` runtime natives
> - `MSB3030` apphost-not-found for `Mcg.AgentOrchestrator.IsolatedDotnetProbe` on the
>   Infrastructure arm
>
> This also disposes of the "nested projects" lead below. Those three arms are not failing because
> they are nested; they are failing because their project paths are the longest, and combined with
> the lease's artifacts root the copy destinations exceed `MAX_PATH`. The operator's standalone
> reproduction passed because its isolated root was `C:/Users/miles/isoarm` — far shorter than the
> in-gate lease path. That is a difference in the *root length*, not in the gate.
>
> Keep the standalone receipts below; they are still valid evidence that the arms and arguments are
> correct. Disregard the diagnosis.

Attempt `8ab0a29d-0-20260816120002525` ran the new `Theory` and three arms failed:

    Native_MTP_..._in_one_step(project: ".../Infrastructure.Tests/M"…, testClass: "ProcessStartInfoSourceGuardTests")
    Native_MTP_..._in_one_step(project: ".../Infrastructure.Tests/C"…, testClass: "CliArgumentNormalizationTests")
    Native_MTP_..._in_one_step(project: ".../Infrastructure.Tests/P"…, testClass: "ProviderDefaultTests")

The Core.Tests and Dashboard.Tests arms passed.

## The arms pass when run standalone

Operator reproduction of the Cli arm with the Theory's exact argument vector, same isolation
property, same passthrough, from the goal worktree:

    dotnet test --project tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Cli/Mcg.AgentOrchestrator.Infrastructure.Cli.Tests.csproj
      --property:McgIsolatedArtifactsPath=<root> --property:BuildInParallel=false
      -- --filter-class "*CliArgumentNormalizationTests*" --minimum-expected-tests 1 --no-ansi --progress off

    total: 16, failed: 0, succeeded: 16

ProviderEnvironment.Tests (18/18) and Infrastructure.Tests (8/8, class-filtered) were likewise
green standalone earlier today at `722d0fee`, recorded in
`8ab0a29d-all-five-projects.md`. The three arms therefore fail *only* when executed nested inside
the acceptance gate. The class names are correct and all three classes exist in the projects named.

## The blocking problem is that the failure is undiagnosable

Every failing arm reports exactly:

    dotnet test --project <path> exited 1.

The TRX carries no `StdOut`, the attempt's `err.log` contains no error, and the assertion discards
the child's captured output. There is no way to determine the in-gate cause from the record —
not by the Developer, who cannot run test hosts, and not by the operator, because the failure does
not reproduce outside the gate.

**This is now the first thing to fix.** The test already redirects both streams
(`RedirectStandardOutput = true`, `RedirectStandardError = true`); include them in the assertion
message, along with the apphost exit code and the reported test total. Until then every round
spends itself rediscovering that `exited 1` means nothing.

It was flagged twice before, at `8ab0a29d-maxcpucount-breaks-one-step.md` and in the round-7 note,
when the hidden detail was `Exit code: 5 / Zero tests ran`. That detail is what identified
`-maxcpucount:1`. The same omission is now blocking a harder failure.

## One pattern in the data, offered as a lead and not a cause

The two arms that passed are the two top-level projects. The three that failed are precisely the
three whose project files live *under* `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/`:

    tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj
    tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Cli/...Cli.Tests.csproj
    tests/Mcg.AgentOrchestrator.Infrastructure.Tests/ProviderEnvironment/...ProviderEnvironment.Tests.csproj

The outer gate lane is itself running `Infrastructure.Tests`, so the inner `dotnet test` builds
projects rooted in the tree whose apphost is currently executing.

This is a correlation across five data points, not a demonstrated mechanism. A previous operator
diagnosis of gate self-contention in this repository was wrong, so treat it as the first
hypothesis to test once the output is captured — not as the answer. Capture the output first; it
will name the cause directly.
