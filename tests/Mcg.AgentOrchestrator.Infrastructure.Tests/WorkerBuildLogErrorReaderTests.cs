using Mcg.AgentOrchestrator.Infrastructure;

public sealed class WorkerBuildLogErrorReaderTests
{
    [Xunit.Fact]
    public void NewestRunKeepsDistinctErrorsInOrderAndExcludesWarnings()
    {
        var root = Path.Combine(Path.GetTempPath(), $"worker-build-log-reader-{Guid.NewGuid():N}");
        try
        {
            Write(root, "20260924T100000000Z", "01-Old.log", "old.cs(1,1): error CS0001: old");
            Write(root, "20260924T100001000Z", "01-New.log",
                "new.cs(2,3): error CS0103: missing",
                "new.cs(2,3): error CS0103: missing",
                "new.cs(3,4): warning CS0168: unused");
            Write(root, "20260924T100001000Z", "02-New.log",
                "build.proj(4,5): error MSB1001: bad switch",
                "sdk.props(6,7): error NETSDK1005: missing assets");

            var errors = Xunit.Assert.IsType<WorkerBuildErrors>(WorkerBuildLogErrorReader.ReadNewest(root));

            Xunit.Assert.Equal(3, errors.Total);
            Xunit.Assert.Equal(new[]
            {
                "new.cs(2,3): error CS0103: missing",
                "build.proj(4,5): error MSB1001: bad switch",
                "sdk.props(6,7): error NETSDK1005: missing assets"
            }, errors.Lines);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Xunit.Fact]
    public void MissingUnreadableAndErrorFreeLogsYieldNoErrors()
    {
        var root = Path.Combine(Path.GetTempPath(), $"worker-build-log-reader-{Guid.NewGuid():N}");
        try
        {
            Xunit.Assert.Null(WorkerBuildLogErrorReader.ReadNewest(root));
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, "worker-build-logs"), "unreadable");
            Xunit.Assert.Null(WorkerBuildLogErrorReader.ReadNewest(root));
            File.Delete(Path.Combine(root, "worker-build-logs"));
            Write(root, "20260924T100001000Z", "01-App.log", "file.cs(1,2): warning CS0168: unused");
            Xunit.Assert.Null(WorkerBuildLogErrorReader.ReadNewest(root));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Xunit.Fact]
    public void ErrorFeedbackAndEscalationHaveSeparateVisibleCaps()
    {
        var root = Path.Combine(Path.GetTempPath(), $"worker-build-log-reader-{Guid.NewGuid():N}");
        try
        {
            Write(root, "20260924T100001000Z", "01-App.log",
                Enumerable.Range(1, 55).Select(index => $"file.cs({index},1): error CS0103: missing {index}").ToArray());

            var errors = Xunit.Assert.IsType<WorkerBuildErrors>(WorkerBuildLogErrorReader.ReadNewest(root));

            Xunit.Assert.Equal(55, errors.Total);
            Xunit.Assert.Equal(50, errors.Lines.Count);
            Xunit.Assert.Contains("35 additional error lines omitted.", errors.Format(20), StringComparison.Ordinal);
            Xunit.Assert.Contains("5 additional error lines omitted.", errors.Format(50), StringComparison.Ordinal);
            Xunit.Assert.DoesNotContain("missing 21", errors.Format(20), StringComparison.Ordinal);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static void Write(string root, string run, string log, params string[] lines)
    {
        var directory = Path.Combine(root, "worker-build-logs", run);
        Directory.CreateDirectory(directory);
        File.WriteAllLines(Path.Combine(directory, log), lines);
    }
}
