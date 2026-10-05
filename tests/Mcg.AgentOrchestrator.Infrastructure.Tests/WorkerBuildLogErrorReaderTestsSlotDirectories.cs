using Mcg.AgentOrchestrator.Infrastructure;

// Every case owns its temporary goal root and can run in parallel.
public sealed class WorkerBuildLogErrorReaderTestsSlotDirectories
{
    private const string OlderRun = "20261005T070000000Z-1-aaaaaaaa";
    private const string NewerRun = "20261005T070001000Z-2-bbbbbbbb";
    private const string CompilerError = "x.cs(1,2): error CS0103: missing";

    [Xunit.Fact]
    public void NewerSlotRunReturnsErrorsInsteadOfOlderErrorFreeCanonicalRun()
    {
        var root = CreateRoot();
        try
        {
            var canonical = Path.Combine(root, "artifacts");
            Write(canonical, OlderRun, "x.cs(1,2): warning CS0168: unused");
            Write(Path.Combine(root, "artifacts-build-1"), NewerRun, CompilerError);

            var errors = Assert.IsType<WorkerBuildErrors>(WorkerBuildLogErrorReader.ReadNewest(canonical));

            Assert.Equal(1, errors.Total);
            Assert.Equal(CompilerError, Assert.Single(errors.Lines));
        }
        finally { Directory.Delete(root, true); }
    }

    [Xunit.Fact]
    public void NewestRunAcrossMultipleSlotsWinsByRunName()
    {
        var root = CreateRoot();
        try
        {
            var canonical = Path.Combine(root, "artifacts");
            Write(Path.Combine(root, "artifacts-build-1"), NewerRun, CompilerError);
            Write(Path.Combine(root, "artifacts-build-2"), OlderRun, "old.cs(1,2): error CS0103: old");

            var errors = Assert.IsType<WorkerBuildErrors>(
                WorkerBuildLogErrorReader.ReadNewest(canonical + Path.DirectorySeparatorChar));

            Assert.Equal(CompilerError, Assert.Single(errors.Lines));
        }
        finally { Directory.Delete(root, true); }
    }

    [Xunit.Fact]
    public void NewerErrorFreeCanonicalRunDoesNotReturnOlderSlotErrors()
    {
        var root = CreateRoot();
        try
        {
            var canonical = Path.Combine(root, "artifacts");
            Write(Path.Combine(root, "artifacts-build-1"), OlderRun, CompilerError);
            Write(canonical, NewerRun, "x.cs(1,2): warning CS0168: unused");

            Assert.Null(WorkerBuildLogErrorReader.ReadNewest(canonical));
        }
        finally { Directory.Delete(root, true); }
    }

    [Xunit.Theory]
    [Xunit.InlineData("artifacts-build-")]
    [Xunit.InlineData("artifacts-build-x")]
    [Xunit.InlineData("artifacts-build-1-old")]
    [Xunit.InlineData("other-build-1")]
    public void NonSlotSiblingIsIgnored(string siblingName)
    {
        var root = CreateRoot();
        try
        {
            var canonical = Path.Combine(root, "artifacts");
            Write(canonical, OlderRun, "x.cs(1,2): warning CS0168: unused");
            Write(Path.Combine(root, siblingName), NewerRun, CompilerError);

            Assert.Null(WorkerBuildLogErrorReader.ReadNewest(canonical));
        }
        finally { Directory.Delete(root, true); }
    }

    private static string CreateRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), $"worker-build-slot-logs-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }

    private static void Write(string artifacts, string run, params string[] lines)
    {
        var directory = Path.Combine(artifacts, "worker-build-logs", run);
        Directory.CreateDirectory(directory);
        File.WriteAllLines(Path.Combine(directory, "01-App.log"), lines);
    }
}
