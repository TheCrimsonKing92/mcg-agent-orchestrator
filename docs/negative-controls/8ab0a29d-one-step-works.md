# Operator execution 2026-08-16: one-step works, with one exclusion added

Follows `8ab0a29d-argv.md`. The `McgIsolatedArtifactsPath` approach is correct. It needs one
additional change to be usable in a worktree that has ever built in-tree.

Measured against `f6d46ef4` in `.orchestrator-worktrees/8ab0a29d`, run from that worktree.

## Four arms

| Invocation | Result |
| --- | --- |
| `dotnet test --project <p>` | `UnauthorizedAccessException` at `TestHostBuilder.BuildAsync`, zero tests |
| `dotnet test --project <p> --artifacts-path <path>` | exit 5, `Unknown option '--artifacts-path'`, zero tests |
| `dotnet test --project <p> -p:McgIsolatedArtifactsPath=<path>` | `error CS0579: Duplicate 'TargetFrameworkAttribute'` and seven more, build fails |
| the same plus the in-tree `obj` excluded from default items | **total: 913, succeeded: 913, failed: 0** |

The last row is native one-step MTP execution succeeding with an isolated artifact root. The
objective is achievable.

## Why the third arm fails

Redirecting `BaseIntermediateOutputPath` moves the SDK's implicit exclusion with it. The default
compile glob excludes `$(BaseIntermediateOutputPath)`; once that points outside the project, the
project's own `obj/` is no longer excluded and its previously generated files are globbed into the
compilation. Two `AssemblyInfo.cs` files then define the same assembly attributes.

Confirmed present:

    src/Mcg.AgentOrchestrator.Core/obj/Debug/net10.0/Mcg.AgentOrchestrator.Core.AssemblyInfo.cs

This only reproduces where an in-tree `obj/` already exists. A pristine clone would pass and hide
the defect — and every goal worktree builds in-tree before acceptance ever runs isolated, so the
failing case is the normal one, not the exotic one.

## The remaining change

`Directory.Build.props` must keep the project's own `obj` and `bin` out of default items whenever
the isolated paths are in effect. **Append** to `DefaultItemExcludes`; do not replace it:

    <DefaultItemExcludes>$(DefaultItemExcludes);$(MSBuildProjectDirectory)\obj\**;$(MSBuildProjectDirectory)\bin\**</DefaultItemExcludes>

The 913/913 receipt above was produced with `-p:DefaultItemExcludes="obj/**"`, which *replaces*
the list. That was adequate to prove the cause but is not the correct fix, because it discards the
SDK's standard exclusions. Append, then re-verify.

## Verification command

From this worktree, with any writable isolated root:

    dotnet test --project tests/Mcg.AgentOrchestrator.Core.Tests/Mcg.AgentOrchestrator.Core.Tests.csproj -p:McgIsolatedArtifactsPath=<root>

Expected: a nonzero test total with zero failures. Zero tests, exit 5, or `CS0579` are all
distinct failures with distinct causes, listed above.

## Unrelated hazard fixed on main

Runs in this repository could die before discovery with
`Win32Exception (5): Access is denied` from `AssemblyTempRedirect.IsProcessAlive` inside a
`ModuleInitializer`, reporting `tests_executed=0` with no failing test. Fixed on `main` in
`0b84061a`; filed as backlog `6152e090`. Merge `main` before relying on any zero-test result here.
