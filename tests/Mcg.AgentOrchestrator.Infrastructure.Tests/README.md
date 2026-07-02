# Infrastructure Test Partitions

The full Infrastructure acceptance gate remains `dotnet test tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj`.

For focused operator verification, use the checked-in partition helper:

```powershell
.\scripts\Invoke-RepoScript.ps1 scripts\Invoke-InfrastructureTestPartition.ps1 -List
.\scripts\Invoke-RepoScript.ps1 scripts\Invoke-InfrastructureTestPartition.ps1 -Partition Cli
```

Current high-cost evidence is recorded in `docs/dogfood/timing-evidence.md`: a full Infrastructure run was 565s, while a focused CLI help filter was 23.9s. The largest current Infrastructure test areas are `CliCommandTests`, `WorkerDispatchTests`, `GoalWorktreeTests`, `DashboardRenderingTests`, and `ConductorBatchLoopTests`; the helper exposes stable named filters for those areas without weakening full-suite coverage.
