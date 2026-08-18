# `-maxcpucount:1` is why the one-step contract test fails

The `McgIsolatedArtifactsPath` design and the `DefaultItemExcludes` addition are both correct and
working. `MtpTestRunnerScriptTests.Native_MTP_dotnet_test_runs_a_repository_project_in_one_step`
fails because of one argument in the invocation the test itself constructs.

Measured by the operator against `e2d5d93ace46` in `.orchestrator-worktrees/8ab0a29d`, run from
that worktree, target
`tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj`,
isolation always supplied as `--property:McgIsolatedArtifactsPath=<root>`.

| Invocation | Result |
| --- | --- |
| isolation only, no passthrough | tests execute |
| isolation, `-- --filter-class "*GoalAcceptanceEvidenceBundleTests*" --no-ansi --progress off` | **total: 8, succeeded: 8** |
| the same plus `--property:BuildInParallel=false` | **total: 8, succeeded: 8** |
| the same plus `-maxcpucount:1` | **Zero tests ran, Exit code: 5** |
| Core.Tests, isolation, `-- --no-ansi --progress off` | **total: 920, succeeded: 920** |

Adding `-maxcpucount:1` on its own flips a passing run to `Zero tests ran / Exit code: 5`.
Removing it flips it back. Nothing else in the invocation changes the outcome.

## Why

`-maxcpucount:1` is a single-dash MSBuild switch. The .NET 10 `dotnet test` driver does not
recognise it as its own option, so it survives into the argument vector handed to the test
application, which rejects it. Exit 5 is `InvalidCommandLine` — the same class of failure as the
earlier `Unknown option '--artifacts-path'`, and for the same reason: an argument the driver
forwards that the Microsoft.Testing.Platform apphost does not accept.

The failure is silent about its cause. The apphost reports only `Zero tests ran` and the driver
reports `exited 1`, which is why the acceptance receipt showed
`dotnet test --project ... exited 1.` with nothing further.

## The change

In `MtpTestRunnerScriptTests`, drop `-maxcpucount:1` from the argument list. Keep
`--property:BuildInParallel=false`, which expresses the same intent — serialise the build — and is
proven safe above. If a CPU cap is genuinely required, pass it as a property rather than a
single-dash switch; anything the driver does not consume becomes apphost argv.

## The production path has the same defect, and it is the one that matters

Round-7 review found the instance this receipt only measured in the test.
`scripts/Invoke-IsolatedDotnet.ps1:742`, in `Get-OneStepMtpDotnetTestArguments`:

    $result.Add("--property:McgIsolatedArtifactsPath=$ArtifactsPath")
    $result.Add("-maxcpucount:$(Get-BuildMaxCpuCount)")
    $result.Add("--property:BuildInParallel=false")
    $result.Add("--")

Every non-reuse one-step invocation therefore carries the switch. Removing it from
`MtpTestRunnerScriptTests` fixed the test and left the real path broken — the test was masking the
defect rather than detecting it.

**The value is irrelevant; the switch is the defect.** Measured at `f3f8e2ab058b`:

| Invocation | Result |
| --- | --- |
| `-maxcpucount:1` | Zero tests ran, Exit code: 5 |
| `-maxcpucount:4` | Zero tests ran, Exit code: 5 |
| switch absent | total 8, succeeded 8 |

So changing `Get-BuildMaxCpuCount`'s default is **not** a fix. Line 742 must go.

`--property:BuildInParallel=false` on the following line already expresses the intent and is proven
safe. Keep it.

Scope the removal to the one-step argument builder only. `dotnet build` accepts `-maxcpucount`
perfectly well, so `Get-BuildMaxCpuCount` and its other call sites in the two-phase path are fine
and must not be disturbed. The rule is narrow: under `dotnet test`, anything the driver does not
consume becomes the test application's argv, and single-dash MSBuild switches are not consumed.

Check whether the reuse path builds its arguments separately and whether it carries the same
switch.

## Also worth fixing: the assertion discards its own diagnosis

The test asserts on the exit code and reports `dotnet test ... exited 1.` The captured stdout
contains `Exit code: 5` and `Zero tests ran`, which name the failure class precisely, but the
assertion message drops them. Three acceptance rounds reported the same opaque line while the
discriminating detail sat in output the test had already captured.

Include the captured output, or at minimum the apphost exit code and the test total, in the
failure message. `Zero tests ran / exit 5`, `UnauthorizedAccessException`, and `CS0579` are three
distinct causes that currently render identically.

## Confirmed working, do not revisit

- `TestingPlatformDotnetTestSupport` absent — correct; MTP v2 rejects the VSTest path on .NET 10.
- `global.json` `test` opt-in present — necessary and active.
- `McgIsolatedArtifactsPath` mapping to `BaseOutputPath` / `BaseIntermediateOutputPath` /
  `MSBuildProjectExtensionsPath` — correct, avoids the SDK-reserved `ArtifactsPath` that the
  driver forwards as `--artifacts-path`.
- `DefaultItemExcludes` appending `$(MSBuildProjectDirectory)\obj\**` and `\bin\**` — correct and
  required; without it the in-tree `obj` is globbed and the build fails with `CS0579`.
