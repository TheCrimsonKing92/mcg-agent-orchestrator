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
        var snapshots = SyntheticProcessIds(incidentalCount, lane: 0)
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
    public void SyntheticProcessIds_SkipCurrentProcessIdAndPreserveRequestedCount()
    {
        var ids = SyntheticProcessIds(1_000, lane: 0);

        Assert.Equal(1_000, ids.Count);
        Assert.Equal(ids.Count, ids.Distinct().Count());
        Assert.DoesNotContain(Environment.ProcessId, ids);
        Assert.True(ids.SequenceEqual(ids.Order()));

        var firstCandidate = Environment.ProcessId;
        var idsWithForcedCollision = SyntheticProcessIdsFrom(firstCandidate, 5);

        Assert.Equal(5, idsWithForcedCollision.Count);
        Assert.Equal(5, idsWithForcedCollision.Distinct().Count());
        Assert.DoesNotContain(Environment.ProcessId, idsWithForcedCollision);
        Assert.Equal(firstCandidate + 5, idsWithForcedCollision[idsWithForcedCollision.Count - 1]);
    }

    [Xunit.Fact]
    public void CommandQuery_SyntheticRangeContainsCurrentProcess_SummaryCountIsOneLower()
    {
        var ids = SyntheticProcessIds(10, lane: 0).ToArray();
        ids[^1] = Environment.ProcessId;
        var snapshots = ids.Select(processId => new RepoProcessCliCommand.ProcessSnapshot(
            processId,
            1,
            $"system-{processId}",
            null,
            null,
            null,
            ProcessInspectionStatus.AccessDenied)).ToArray();
        var output = new StringWriter();

        RepoProcessCliCommand.PrintInfo(
            ["repo-process-info", "--command-contains", "schedule.ps1"],
            output,
            _ => snapshots);

        Assert.Contains("PROCESS_QUERY_SUMMARY operation=filter-incidental count=9 statuses=AccessDenied:9", output.ToString());
        Assert.DoesNotContain($"{Environment.ProcessId}:system-", output.ToString());
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

    [Xunit.Fact]
    public void IncludeChildren_RecycledParentEdge_ExcludesChildAndPrintsRejectionDiagnostic()
    {
        var output = new StringWriter();
        var parentStartedAt = DateTimeOffset.Parse("2026-09-03T12:00:00Z");

        RepoProcessCliCommand.PrintInfo(
            ["repo-process-info", "--id", "31292", "--include-children"],
            output,
            _ =>
            [
                Process(31_292, 1, "conhost", parentStartedAt),
                Process(1_056, 31_292, "OneDrive.Sync.Service", DateTimeOffset.Parse("2026-09-02T12:00:00Z")),
                Process(2_000, 1_056, "alleged-grandchild", parentStartedAt.AddMinutes(1))
            ]);

        var text = output.ToString();
        Assert.Contains("PROCESS id=31292 ", text);
        Assert.DoesNotContain("PROCESS id=1056 ", text);
        Assert.DoesNotContain("PROCESS id=2000 ", text);
        Assert.Contains(
            "PROCESS_EDGE_REJECTED parent=31292 parent-anchor=31292 " +
            "parent-anchor-created=2026-09-03T12:00:00.0000000+00:00 " +
            "child=1056 child-name=OneDrive.Sync.Service child-created=2026-09-02T12:00:00.0000000+00:00",
            text);
        Assert.Contains("PROCESS_EDGE_SUMMARY rejected=1 unverified=0 truncated=0", text);
    }

    [Xunit.Fact]
    public void IncludeChildren_ValidEdge_PrintsChildUnderParent()
    {
        var output = new StringWriter();
        var parentStartedAt = DateTimeOffset.Parse("2026-09-03T12:00:00Z");

        RepoProcessCliCommand.PrintInfo(
            ["repo-process-info", "--id", "100", "--include-children"],
            output,
            _ =>
            [
                Process(100, 1, "parent", parentStartedAt),
                Process(101, 100, "child", parentStartedAt.AddSeconds(1))
            ]);

        var text = output.ToString();
        Assert.Contains("PROCESS id=100 ", text);
        Assert.Contains("PROCESS id=101 parent=100", text);
        Assert.DoesNotContain("PROCESS_EDGE_", text);
    }

    [Xunit.Fact]
    public void ParentIdQuery_MissingParentIdentity_PrintsUnverifiedEdgeInsteadOfSilentInclude()
    {
        var output = new StringWriter();

        RepoProcessCliCommand.PrintInfo(
            ["repo-process-info", "--parent-id", "100"],
            output,
            _ =>
            [
                Process(101, 100, "child", DateTimeOffset.Parse("2026-09-03T12:00:01Z"))
            ]);

        var text = output.ToString();
        Assert.Contains("PROCESS id=101 parent=100", text);
        Assert.Contains(
            "PROCESS_EDGE_UNVERIFIED parent=100 parent-anchor=unknown parent-anchor-created=unknown child=101 " +
            "child-name=child child-created=2026-09-03T12:00:01.0000000+00:00",
            text);
        Assert.Contains("PROCESS_EDGE_SUMMARY rejected=0 unverified=1 truncated=0", text);
    }

    [Xunit.Fact]
    public void IncludeChildren_ManyUnverifiedEdges_EmitsBoundedDiagnostic()
    {
        var output = new StringWriter();
        var parentStartedAt = DateTimeOffset.Parse("2026-09-03T12:00:00Z");
        var snapshots = SyntheticProcessIds(10, lane: 1)
            .Select(processId => new RepoProcessCliCommand.ProcessSnapshot(
                processId,
                100,
                $"child-{processId}",
                null,
                null,
                null,
                ProcessInspectionStatus.AccessDenied))
            .Prepend(Process(100, 1, "parent", parentStartedAt))
            .ToArray();

        RepoProcessCliCommand.PrintInfo(
            ["repo-process-info", "--id", "100", "--include-children"],
            output,
            _ => snapshots);

        var lines = output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(8, lines.Count(line => line.StartsWith("PROCESS_EDGE_UNVERIFIED", StringComparison.Ordinal)));
        Assert.Contains("PROCESS_EDGE_SUMMARY rejected=0 unverified=10 truncated=2", lines);
    }

    [Xunit.Fact]
    public void IncludeChildren_UnreadableIntermediate_DoesNotLaunderOlderGrandchild()
    {
        var output = new StringWriter();
        var parentStartedAt = DateTimeOffset.Parse("2026-09-03T12:00:00Z");

        RepoProcessCliCommand.PrintInfo(
            ["repo-process-info", "--id", "100", "--include-children"],
            output,
            _ =>
            [
                Process(100, 1, "parent", parentStartedAt),
                new RepoProcessCliCommand.ProcessSnapshot(
                    101,
                    100,
                    "unreadable-child",
                    null,
                    null,
                    null,
                    ProcessInspectionStatus.AccessDenied),
                Process(102, 101, "older-grandchild", parentStartedAt.AddDays(-1))
            ]);

        var text = output.ToString();
        Assert.Contains("PROCESS id=101 parent=100", text);
        Assert.DoesNotContain("PROCESS id=102 ", text);
        Assert.Contains("PROCESS_EDGE_UNVERIFIED parent=100", text);
        Assert.Contains("PROCESS_EDGE_REJECTED parent=101 parent-anchor=100", text);
    }

    private static RepoProcessCliCommand.ProcessSnapshot Process(
        int processId,
        int parentProcessId,
        string name,
        DateTimeOffset startedAt) =>
        new(
            processId,
            parentProcessId,
            name,
            Path.Combine("fixture", name + ".exe"),
            startedAt,
            name,
            ProcessInspectionStatus.Available);

    private static RepoProcessCliCommand.ProcessSnapshot Snapshot(DateTimeOffset startedAt) =>
        new(
            42,
            1,
            "dotnet",
            Path.Combine(Path.GetTempPath(), "dotnet.exe"),
            startedAt,
            "dotnet App.dll conduct --loop",
            ProcessInspectionStatus.Available);

    private const int SyntheticProcessIdBase = 1_000_000_000;
    private const int SyntheticProcessIdLaneWidth = 1_000_000;

    private static IReadOnlyList<int> SyntheticProcessIds(int count, int lane)
    {
        if (lane is < 0 or > 9)
        {
            throw new ArgumentOutOfRangeException(nameof(lane));
        }

        if (count < 0 || count >= SyntheticProcessIdLaneWidth)
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }

        return SyntheticProcessIdsFrom(SyntheticProcessIdBase + lane * SyntheticProcessIdLaneWidth, count);
    }

    private static IReadOnlyList<int> SyntheticProcessIdsFrom(int firstCandidate, int count)
    {
        var ids = new List<int>(count);
        for (long candidate = firstCandidate; ids.Count < count; candidate++)
        {
            if (candidate == Environment.ProcessId)
            {
                continue;
            }

            if (candidate > int.MaxValue)
            {
                throw new ArgumentOutOfRangeException(nameof(count));
            }

            ids.Add((int)candidate);
        }

        return ids;
    }
}
