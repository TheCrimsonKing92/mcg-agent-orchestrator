using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class RepoProcessCliCommandTests
{
    [Xunit.Fact]
    public void StopRevalidation_RecycledPid_RefusesAction()
    {
        var recorded = Snapshot(DateTimeOffset.Parse("2026-08-26T12:00:00Z"));
        var recycled = Snapshot(DateTimeOffset.Parse("2026-08-26T12:01:00Z"));

        var reason = RepoProcessCliCommand.EvaluateStopRevalidation(recorded, recycled, ["conduct"]);

        Assert.Equal("identity-mismatch", reason);
    }

    [Xunit.Fact]
    public void StopRevalidation_UnreadableProcess_RefusesAction()
    {
        var recorded = Snapshot(DateTimeOffset.Parse("2026-08-26T12:00:00Z"));
        var unreadable = recorded with
        {
            CommandLine = null,
            InspectionStatus = ProcessInspectionStatus.AccessDenied
        };

        var reason = RepoProcessCliCommand.EvaluateStopRevalidation(recorded, unreadable, ["conduct"]);

        Assert.Equal("inspection-unavailable", reason);
    }

    [Xunit.Fact]
    public void LockReport_UnreadableCandidate_DoesNotReportFree()
    {
        var output = new StringWriter();
        RepoProcessCliCommand.PrintInfo(
            ["repo-process-info", "--locks"],
            output,
            _ =>
            [
                new RepoProcessCliCommand.ProcessSnapshot(
                    42,
                    1,
                    "dotnet",
                    null,
                    null,
                    null,
                    ProcessInspectionStatus.AccessDenied)
            ]);

        Assert.Contains("PROCESS_QUERY_UNAVAILABLE operation=filter id=42", output.ToString());
        Assert.DoesNotContain("in-tree build lock is FREE", output.ToString());
    }

    [Xunit.Fact]
    public void CommandQuery_CurrentInvocation_IsExcluded()
    {
        var output = new StringWriter();
        RepoProcessCliCommand.PrintInfo(
            ["repo-process-info", "--command-contains", "schedule.ps1"],
            output,
            _ =>
            [
                Snapshot(DateTimeOffset.Parse("2026-08-26T12:00:00Z")) with
                {
                    ProcessId = Environment.ProcessId,
                    CommandLine = "dotnet App.dll repo-process-info --command-contains schedule.ps1"
                },
                Snapshot(DateTimeOffset.Parse("2026-08-26T11:00:00Z")) with
                {
                    ProcessId = 43,
                    Name = "pwsh",
                    CommandLine = "pwsh -File schedule.ps1"
                }
            ]);

        Assert.DoesNotContain($"PROCESS id={Environment.ProcessId}", output.ToString());
        Assert.Contains("PROCESS id=43", output.ToString());
    }

    [Xunit.Fact]
    public void CommandQuery_UnreadableNamedCandidate_IsTypedUnavailable()
    {
        var output = new StringWriter();
        RepoProcessCliCommand.PrintInfo(
            ["repo-process-info", "--name", "pwsh", "--command-contains", "schedule.ps1"],
            output,
            _ =>
            [
                new RepoProcessCliCommand.ProcessSnapshot(
                    43,
                    1,
                    "pwsh",
                    null,
                    null,
                    null,
                    ProcessInspectionStatus.PartialRead)
            ]);

        Assert.Contains("PROCESS_QUERY_UNAVAILABLE operation=filter id=43", output.ToString());
        Assert.DoesNotContain("No matching repo processes found", output.ToString());
    }

    [Xunit.Theory]
    [Xunit.InlineData(10)]
    [Xunit.InlineData(100)]
    [Xunit.InlineData(1_000)]
    public void CommandQuery_ManyIncidentalFailures_EmitsBoundedSummary(int incidentalCount)
    {
        var output = new StringWriter();
        var snapshots = Enumerable.Range(1000, incidentalCount)
            .Select(processId => new RepoProcessCliCommand.ProcessSnapshot(
                processId,
                1,
                $"system-{processId}",
                null,
                null,
                null,
                ProcessInspectionStatus.AccessDenied))
            .Append(new RepoProcessCliCommand.ProcessSnapshot(
                42,
                1,
                "pwsh",
                null,
                null,
                null,
                ProcessInspectionStatus.AccessDenied))
            .ToArray();

        RepoProcessCliCommand.PrintInfo(
            ["repo-process-info", "--command-contains", "schedule.ps1"],
            output,
            _ => snapshots);

        var text = output.ToString();
        Assert.Contains("PROCESS_QUERY_UNAVAILABLE operation=filter id=42 name=pwsh status=AccessDenied", text);
        Assert.Contains($"PROCESS_QUERY_SUMMARY operation=filter-incidental count={incidentalCount}", text);
        Assert.DoesNotContain("operation=filter id=1000", text);
        Assert.True(text.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries).Length <= 3, text);
        Assert.DoesNotContain("No matching repo processes found", text);
    }

    [Xunit.Fact]
    public void NameQuery_DottedName_PreservesSeed()
    {
        RepoProcessCliCommand.ProcessSnapshotQuery? captured = null;

        RepoProcessCliCommand.PrintInfo(
            ["repo-process-info", "--name", "Mcg.AgentOrchestrator.App"],
            TextWriter.Null,
            query =>
            {
                captured = query;
                return [];
            });

        Assert.NotNull(captured);
        Assert.Contains("Mcg.AgentOrchestrator.App", captured.ProcessNames);
    }

    [Xunit.Fact]
    public void LocksQuery_OrchestratorFamily_UsesPrefixSeed()
    {
        RepoProcessCliCommand.ProcessSnapshotQuery? captured = null;

        RepoProcessCliCommand.PrintInfo(
            ["repo-process-info", "--locks"],
            TextWriter.Null,
            query =>
            {
                captured = query;
                return [];
            });

        Assert.NotNull(captured);
        Assert.Contains("Mcg.AgentOrchestrator*", captured.ProcessNames);
    }

    private static RepoProcessCliCommand.ProcessSnapshot Snapshot(DateTimeOffset startedAt) =>
        new(
            42,
            1,
            "dotnet",
            Path.Combine(Path.GetTempPath(), "dotnet.exe"),
            startedAt,
            "dotnet App.dll conduct --loop",
            ProcessInspectionStatus.Available);
}
