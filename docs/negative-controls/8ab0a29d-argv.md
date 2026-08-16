# Operator execution 2026-08-16: exit 5 root cause is an unsupported `--artifacts-path`

Supplements `docs/negative-controls/8ab0a29d.md`. That file established that the `global.json`
opt-in is active and necessary but that one-step `dotnet test` still exits 5 with zero tests. This
records the controller/client diagnostics the Developer's blocker asked for and identifies the
rejected argument.

All arms were run from the goal worktree, `.orchestrator-worktrees/8ab0a29d`, because `global.json`
resolves from the current directory. Running from the repository root reads main's `global.json`,
which has no `test` object, and silently invalidates the control.

## The argument vector the SDK driver passes

Captured from inside the test process via `Environment.GetCommandLineArgs()` at module init:

    Mcg.AgentOrchestrator.Core.Tests.dll
      | --artifacts-path | <path>
      | --server | dotnettestcli
      | --dotnet-test-pipe | testingplatform.pipe.<guid>

`--artifacts-path` appears only when the caller supplies it. `--server` and `--dotnet-test-pipe`
are always added by the driver.

## Which argument is rejected

Running the apphost directly:

    Mcg.AgentOrchestrator.Core.Tests.exe --artifacts-path <path> --list-tests
      -> Unknown option '--artifacts-path'

    Mcg.AgentOrchestrator.Core.Tests.exe --server dotnettestcli --dotnet-test-pipe testpipe123
      -> accepted; enters server mode and blocks awaiting the named pipe

So `--server` and `--dotnet-test-pipe` are supported despite being undocumented in `--help`.
`--artifacts-path` is not implemented by the xunit MTP v2 runner in use
(`xunit.v3.mtp-v2` 3.2.2). It is the sole cause of the `InvalidCommandLine` exit.

## Both one-step arms fail, for different reasons

| Arm | Result |
| --- | --- |
| `dotnet test --project <p> --artifacts-path <unique>` | exit 5, `Unknown option '--artifacts-path'`, zero tests |
| `dotnet test --project <p>` (no isolation) | exit -532462766, `UnhandledException: System.UnauthorizedAccessException: Access to the path is denied` at `TestHostBuilder.BuildAsync` |

This is the comparison the blocker requested, completed. The isolation the acceptance lane
requires is exactly the option the runner does not support, and omitting it leaves the host
unable to write its default artifacts location.

## A hypothesis that was tested and is false

The failing run's only output was our own
`assembly-temp-redirect selected=...\Temp\Low\mcg-tests rejected=<none>`, written to stderr by
`AssemblyTempRedirect.Install`. The obvious theory was that the driver treats any child stderr
output as an error, given the `error: 1` in the summary.

Tested by changing that call to `Console.Out.WriteLine` and re-running. Result was identical:
zero tests, exit 5, `error: 1`. The diagnostic line is incidental — it is simply the only thing
the process emits before failing. The change was reverted; it is recorded here so nobody retests
it.

## What this does not establish

Every arm ran at the launching shell's integrity level. Nothing here measures controller or client
behaviour under a genuine Low-integrity dispatch, which in production comes from
`DispatchProcessHost`. The artifact-root question is answered; the Low-integrity question is not,
and no bespoke low-integrity script should be written to answer it — see the deleted
`scripts/Test-LowIntegrity.ps1` and the deterministic coverage in
`tests/Mcg.AgentOrchestrator.Infrastructure.Tests/WorkerDispatchTestsSandboxLowIntegrity.cs`.

## Consequence for the goal

The runner cannot accept isolation through the SDK-reserved `ArtifactsPath` property. The bounded
repository fix is to pass `McgIsolatedArtifactsPath` instead; `Directory.Build.props` maps that value
to project-scoped `BaseOutputPath`, `BaseIntermediateOutputPath`, and `MSBuildProjectExtensionsPath`.
This keeps all build output below the isolated root without causing the .NET 10 driver to append
`--artifacts-path` to the test application argument vector. The operator receipt above remains the
negative control for the rejected SDK-reserved path; the acceptance lane owns the positive rerun.
