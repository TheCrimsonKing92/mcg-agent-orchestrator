# Worker build retry investigation

Input candidate: `87f085a03244c8ba7c42b0964f0f29b5b2b21179`, tree `52369f872f9c80f30eefa247e767cfe25e02a9ce`.
Packaged criterion-retry feedback includes five executed rows, no skips, TRX SHA256 `613701D4F2C10D82874E32B55267F74CC312331D07F2A1AF203764DEEB6893F2`.
The three canonical-host identity mutations passed and are unchanged.

## Recovery fixture

`WorkerBuildRequestRecoveryTests.ManagedHostDeathAfterCompilerStart_ReloadRetainsTerminalObligationWithoutRelaunch`
failed with `Owned compiler did not signal the named readiness event.` Its retained compiler output shows successful restore followed by `The system cannot find the path specified.` and MSB3073 exit 1 before readiness. The wait does not establish a hung compiler.

Local diagnostic controls (no test runner, no compilation) invoked the same resolved PowerShell executable through `cmd.exe /Q /D /C <batch-file>`, asynchronously draining stdout/stderr and awaiting the owned process. Generated files and empty directories were deleted afterward.

| Variable | Observation |
| --- | --- |
| Original quoted named-event command in a short batch path; release event already set | Exit 0, readiness event signaled |
| Command printing `fixture-command-entered`, TEMP length 139, generated batch path length 201 | Exit 0, expected output |
| Identical command, TEMP length 207, generated batch path length 269 | Exit 1, no stdout, `The system cannot find the path specified.` |
| Identical command, shortened TEMP length 187, generated batch path length 249 | Exit 0, expected output |

The retained invocation's TEMP directory was 207 characters. [MSBuild Exec source](https://source.dot.net/Microsoft.Build.Tasks.Core/Exec.cs.html) creates a `.exec.cmd` temporary file and runs it through the command processor. The control uses its `MSBuildTemp<user>/tmp<guid>.exec.cmd` layout. This isolates batch-path length from command quoting; it does not claim a post-change integration pass.

`WorkerBuildRequestHost.Execute` now creates `artifacts/tmp/<invocation>` instead of nesting TEMP inside `artifacts/worker-host-builds/<invocation>/temp`. For the reported root this removes 20 characters, making the controlled 269-character batch path 249 characters. The directory remains under the same artifact owner and existing LowIL labeling. `BuildStartInfo` passes it only to the owned compiler through TEMP/TMP. Custody, slot acquisition, process registration, resume authorization and terminal handling are unchanged. The existing readiness gate and three-minute failsafe are unchanged.

## LowIL compilation

`WorkerBuildRequestIntegrationTests.OrdinaryHelperLowIntegrityPeerAndCompilerRetainSandbox` observed the required LowIL worker/compiler tokens, then failed `Expected: Success; Actual: CompilerFailure`, compiler exit 1. The retained tail starts inside NuGet's `RuntimeEnvironmentHelper.GetCurrentProcessFilePath` stack, omitting the first exception type/message. [NuGet source](https://source.dot.net/NuGet.Common/RuntimeEnvironmentHelper.cs.html) queries `Process.MainModule.FileName` there.

A separate local diagnostic launched two PowerShell children that queried their own `Process.MainModule.FileName`: one at ordinary integrity, one after executing the repository's `DispatchProcessHost.DropToLowScript`. Both exited 0 and returned their image path. This does not reproduce the compiler's startup/runtime conditions and does not explain its failure. No process or token access policy was changed.

Cause remains undetermined. `WorkerBuildRequestFixture.DescribeHostBuildDiagnostics` now scans at most 65,536 initial characters, emits at most 2,048 characters beginning with the first error/exception line, and retains the existing 4,096-character tail. Acceptance must execute the original LowIL control to obtain that missing diagnostic before selecting a causal repair.

## Outstanding verification

Developer invoked `scripts/Invoke-RepoScript.ps1 scripts/Invoke-WorkerBuildCheck.ps1 src/Mcg.AgentOrchestrator.App/Mcg.AgentOrchestrator.App.csproj tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj` twice:

- First: exit 1, four-project closure, helper reported `worktree identity changed during worker build` after compilation. The investigation document was edited while it ran. Its receipt is invalid. Logs: `goals/35bcb7de/artifacts/worker-build-logs/20261020T043716750Z-61524-81a2fd56` under the configured isolated build root.
- Second, with contents held unchanged: exit 1, elapsed 300149 ms, captured characters 0, logs unavailable. Helper reported `Timed out waiting for build lease execution lock` on `build-1.lock`. This legacy helper output is not a trusted host-phase receipt. No current successful build is claimed and no third retry was started.

`git diff --check` passed. Acceptance owns a successful current build, execution of both original failing methods, all authority/recovery/compatibility and negative controls, and the normal gate. No test execution or recovered pass is claimed by this investigation. Independent cross-family implementation review remains pending. Criteria 1 and 3 remain unmet until the LowIL failure is resolved and execution proves the recovery control reaches its host-death assertions. Natural post-activation observation remains pending in `ebd73904`.
