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

Partition names and filters come from `config/acceptance-manifest.json`; `-List` prints the current valid names. Results default under `%LOCALAPPDATA%\Temp\Low\mcg-tests`. Exit codes 20-29 distinguish manifest, partition, results-directory, build, runner-artifact, target, runner, zero-test, cleanup, and timeout failures; a managed runner's non-zero exit is otherwise propagated verbatim. Clean run directories are removed and failed runs are retained for diagnosis. Every new run directory receives an atomic `.mtp-run-ownership.json` sidecar containing its acceptance-attempt id (or `unowned`), process, machine, creation time, and run label. Scheduled conductor maintenance reclaims only sidecar-owned runs that resolve through canonical attempt metadata to a terminal goal and reconciled attempt; it preserves the final and last-failing attempts, shares the attempt-writer lease, and applies a 14-day or 100-directory bound. Legacy, unowned, ambiguous, non-terminal, live, or locked runs remain retained.
