---
name: dotnet-windows-build-hygiene
description: Handle Windows and .NET build/test hygiene for mcg-agent-orchestrator. Use for dotnet build or test failures, CS2012 file locks, VBCSCompiler/MSBuild server issues, dashboard apphost locks, PowerShell quoting, worktree-safe verification, exact process handling, and minimal-output command selection.
---

# .NET Windows Build Hygiene

Use this skill when building, testing, or diagnosing this repository on Windows.

## Command Discipline

- Prefer narrow `dotnet test` filters over full-suite runs until a change is ready.
- Quote filters containing `|`, for example `--filter 'AgentCatalog|WorkerDispatch'`.
- Use minimal verbosity first; expand output only for failing tests.
- Run commands from the relevant worktree, not the shared root, when validating a goal branch.
- Preserve user changes; do not clean, reset, or remove unrelated files.

## File Locks

If build/test fails with CS2012, "file in use", or locked `bin`/`obj` outputs:

1. Run `dotnet build-server shutdown`.
2. Retry the same narrow command once.
3. If still locked, inspect exact known processes such as `Mcg.AgentOrchestrator.App`, `dotnet`, or the recorded worker pid.
4. Stop only exact known stale processes; never run broad cleanup such as killing every `codex`, `dotnet`, or app process.

## Dashboard And Apphost

- Prefer `.\scripts\Invoke-DashboardBuildTestCycle.ps1 -DashboardUrl <url>` when a running dashboard may lock app binaries.
- For anything that binds a non-loopback address, prefer launching as `dotnet <App.dll>` rather than `dotnet run` or a fresh apphost exe to avoid Windows Firewall prompts.
- Use checked-in dashboard helpers instead of one-off browser/API scripts when they fit the task.

## Verification Shape

- For source-only changes, run focused tests for the changed surface first.
- For shared infrastructure, CLI, dashboard API, or worker dispatch changes, broaden to the relevant project suite.
- Record whether any retry was due to build-server/file-lock hygiene rather than product failure.
