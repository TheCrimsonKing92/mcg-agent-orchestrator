---
name: dotnet-windows-build-hygiene
description: Handle Windows and .NET build/test hygiene for mcg-agent-orchestrator. Use for dotnet build or test failures, CS2012 file locks, VBCSCompiler/MSBuild server issues, PowerShell quoting, worktree-safe verification, exact process handling, and minimal-output command selection.
---

# .NET Windows Build Hygiene

Use this skill when building, testing, or diagnosing this repository on Windows. Follow your role's execution limits; defer prohibited tests to acceptance.

## Command Discipline

- Use `.\scripts\Invoke-WorkerBuildCheck.ps1 <project.csproj> [project.csproj...]` as the normal Developer/Tester build command. It retains complete per-project output in the isolated goal artifacts area and returns a bounded project/error summary. Do not use raw `dotnet build` for routine verification when this helper can express the target.
- Prefer `scripts/Invoke-TestSummary.ps1 -Target <test-project> -Filter 'FullyQualifiedName~<class>'` with narrow filters over full-suite runs until a change is ready. Do not run raw `dotnet test`, which launches the generated native test apphost on Windows.
- Follow the [operator runbook's Codex harness timeout floor](../../../docs/operator-runbook.md): choose the outer collection budget from the invoked helper's documented/internal maximum plus at least 30 seconds of startup and result-reconciliation margin. A helper phase bound does not bound the whole operation.
- Resume the same yielded session while it runs; do not restart or cancel it merely because a collection window expires. Inspect bounded process evidence and retained receipts before claiming a hang. Cancellation, an outer timeout, or a missing final receipt (including TRX) is inconclusive; report terminal build/test failure only from a completed operation's result.
- Build once with `Invoke-WorkerBuildCheck.ps1`, then, when your role permits tests, use `Invoke-TestSummary.ps1` with `-NoBuild` and a narrow `-Filter` in slices. Keep build and broad test runs separate; preserve required verification and conductor gates.
- Quote filters containing `|`, for example `-Filter 'FullyQualifiedName~AgentCatalog|FullyQualifiedName~WorkerDispatch'`.
- Use minimal verbosity first; expand output only for failing tests.
- Run commands from the relevant worktree, not the shared root, when validating a goal branch.
- Preserve user changes; do not clean, reset, or remove unrelated files.

## Diagnostic Escape Hatch

- When the normal helper cannot represent a required diagnostic argument shape, run `.\scripts\Invoke-WorkerBuildDiagnostic.ps1 build <project-or-solution> <diagnostic-arguments...>`. Arguments remain an array; no shell command text is evaluated.
- The diagnostic wrapper redirects complete stdout/stderr to the isolated goal artifacts area and returns only exit/error/warning/character counts, elapsed time, and the log path. Its output is explicitly not routine worker build evidence.
- A worker-requested diagnostic run remains available, but return only the bounded wrapper summary to the model. Never paste or replay the full log unless a specific missing diagnostic must be excerpted.

## File Locks

If build/test fails with CS2012, "file in use", or locked `bin`/`obj` outputs:

1. The Roslyn and MSBuild build servers are disabled repo-wide by `Directory.Build.props` and `Directory.Build.rsp`, so do not run a build-server shutdown; identify the exact process holding the file first.
2. Retry the same narrow command once.
3. If still locked, inspect exact known processes such as `Mcg.AgentOrchestrator.App`, `dotnet`, or the recorded worker pid.
4. Stop only exact known stale processes; never run broad cleanup such as killing every `codex`, `dotnet`, or app process.

## Apphost

- For anything that binds a non-loopback address, prefer launching as `dotnet <App.dll>` rather than `dotnet run` or a fresh apphost exe to avoid Windows Firewall prompts.

## Verification Shape

- For source-only changes, run focused tests for the changed surface first.
- For shared infrastructure, CLI, or worker dispatch changes, broaden to the relevant project suite.
- Record whether any retry was due to build-server/file-lock hygiene rather than product failure.
