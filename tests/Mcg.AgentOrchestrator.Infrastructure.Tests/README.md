# Infrastructure Test Partitions

Run the full Infrastructure project through the checked-in MTP summary helper:

```powershell
.\scripts\Invoke-RepoScript.ps1 scripts\Invoke-TestSummary.ps1 -Target .\tests\Mcg.AgentOrchestrator.Infrastructure.Tests\Mcg.AgentOrchestrator.Infrastructure.Tests.csproj
```

The helper builds by default and launches the Microsoft.Testing.Platform managed assembly through `dotnet`; it never executes the generated native apphost. The .NET 10 SDK refuses the VSTest `dotnet test` target for these projects; `-NoBuild` is an explicit fast-path only when the managed assembly is known current.

For focused operator verification, use the checked-in partition helper:

```powershell
.\scripts\Invoke-RepoScript.ps1 scripts\Invoke-InfrastructureTestPartition.ps1 -List
.\scripts\Invoke-RepoScript.ps1 scripts\Invoke-InfrastructureTestPartition.ps1 -Partition Cli
```

Partition names and filters come from `config/acceptance-manifest.json`; `-List` prints the current valid names. Results default under `%LOCALAPPDATA%\Temp\Low\mcg-tests`. Exit codes 20-29 distinguish manifest, partition, results-directory, build, runner-artifact, target, runner, zero-test, cleanup, and timeout failures; a managed runner's non-zero exit is otherwise propagated verbatim. Clean run directories are removed and failed runs are retained. Every new run directory receives an atomic `.mtp-run-ownership.json` sidecar containing its acceptance-attempt id (or `unowned`), process, machine, creation time, and run label. No generic age purge runs against this shared tree: legacy or unowned directories remain retained until authoritative terminal-goal ownership can be established.
