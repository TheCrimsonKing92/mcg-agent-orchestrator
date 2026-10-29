using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;

// Parallel-safe integration tests: Git, receipts, reports and detached checkouts belong to each fixture.
public sealed class ImpactReplayCommandTests
{
    [Fact]
    public void OneSourceChangeSelectsDependentAndFloorLanesAndReportsSavings()
    {
        using var fixture = new ReplayFixture();
        fixture.AddAttempt("one-source");

        var (report, markdown) = fixture.Run();

        var attempt = Assert.Single(report.Attempts);
        Assert.Equal("one-source", attempt.AttemptId);
        Assert.Equal("Resolved", attempt.SelectionOutcome);
        Assert.Null(attempt.DegradationKind);
        Assert.Equal(new[] { "src/Sample/Alpha.cs" }, attempt.ChangedFiles);
        Assert.Equal(new[] { "AlphaTests" }, attempt.SelectedTestClasses);
        Assert.Equal(new[] { "Alpha lane", "Beta lane", "Chaos gate" }, attempt.RanLanes);
        Assert.Equal(new[] { "Alpha lane", "Chaos gate" }, attempt.WouldRunLanes);
        Assert.Equal(new[] { "Beta lane" }, attempt.SkippedLanes);
        Assert.Equal(23.5, attempt.EstimatedSerialSecondsSaved);
        Assert.Equal(1.0 / 3, attempt.ShareOfLanesSkipped);
        Assert.Equal("default", report.FloorSource);
        Assert.Equal(new[] { "Chaos gate" }, report.FloorLanes);
        Assert.All(attempt.LaneOutcomes, lane => Assert.Equal("Passed", lane.Outcome));
        Assert.Empty(attempt.FailingClasses);
        Assert.True(attempt.MissCheckComplete);
        Assert.Contains("Attempts replayed: 1", markdown);
        Assert.Contains("Total estimated lane-seconds saved: 23.5", markdown);
        Assert.Contains("Median share of lanes skipped: 33.3", markdown);
        Assert.Contains("P90 share of lanes skipped: 33.3", markdown);
    }

    [Fact]
    public void FailureInSkippedLaneIsReportedAsMissWithAttemptClassAndLane()
    {
        using var fixture = new ReplayFixture();
        fixture.AddAttempt("miss-attempt", failingBeta: true);

        var (report, markdown) = fixture.Run();

        var attempt = Assert.Single(report.Attempts);
        Assert.Equal("Resolved", attempt.SelectionOutcome);
        Assert.Equal(new[] { "Beta lane" }, attempt.SkippedLanes);
        var failure = Assert.Single(attempt.FailingClasses);
        Assert.Equal("SampleTests.BetaTests", failure.ClassName);
        Assert.Equal("Beta lane", failure.Lane);
        Assert.Equal(false, failure.WouldRun);
        Assert.True(failure.IsMiss);
        Assert.True(attempt.MissCheckComplete);
        Assert.Equal("Failed", Assert.Single(attempt.LaneOutcomes, lane => lane.Lane == "Beta lane").Outcome);
        Assert.Contains("| miss-attempt | SampleTests.BetaTests | Beta lane |", markdown);
    }

    [Fact]
    public void SixSourceChangesAreCountedAsAbandonedWithoutSkippingAnyLanes()
    {
        using var fixture = new ReplayFixture(changedSourceCount: 6);
        fixture.AddAttempt("over-cap");

        var (report, markdown) = fixture.Run();

        var attempt = Assert.Single(report.Attempts);
        Assert.Equal(6, attempt.ChangedFiles.Count);
        Assert.Equal("Abandoned", attempt.SelectionOutcome);
        Assert.Equal("ChangedSourceFileCount", attempt.DegradationKind);
        Assert.Empty(attempt.SkippedLanes);
        Assert.Equal(attempt.RanLanes.ToArray(), attempt.WouldRunLanes.ToArray());
        Assert.Equal(0, attempt.EstimatedSerialSecondsSaved);
        Assert.Equal(0, attempt.ShareOfLanesSkipped);
        Assert.Contains("- ChangedSourceFileCount: 1", markdown);
        Assert.Contains("Total estimated lane-seconds saved: 0", markdown);
    }

    [Fact]
    public void CommandOnlyWritesRequestedReportsAndRemovesDetachedWorktrees()
    {
        using var fixture = new ReplayFixture();
        fixture.AddAttempt("read-only");
        fixture.Write(".orchestrator/state.db", "opaque state sentinel");
        fixture.Write(".orchestrator/nested/keep.txt", "unchanged receipt sentinel");
        var before = fixture.StateSnapshot();
        var worktreesBefore = fixture.Git("worktree", "list", "--porcelain");
        Assert.False(Directory.Exists(fixture.CheckoutParent));

        var (report, _) = fixture.Run();

        Assert.Single(report.Attempts);
        Assert.Equal(before, fixture.StateSnapshot());
        Assert.Equal(new[] { "impact-replay.json", "impact-replay.md" },
            Directory.GetFiles(fixture.OutputDirectory).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.Equal(worktreesBefore, fixture.Git("worktree", "list", "--porcelain"));
        Assert.True(Directory.Exists(fixture.CheckoutParent));
        Assert.Empty(Directory.EnumerateFileSystemEntries(fixture.CheckoutParent));
        Assert.Empty(report.CleanupErrors);
    }

    private sealed class ReplayFixture : IDisposable
    {
        private const string TestProject = "tests/Mcg.AgentOrchestrator.Infrastructure.Tests";
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
        private readonly string _root = ConductorDriverTests.CreateTempDirectory();
        private readonly string _main;
        private readonly string _branch;
        private string StateDirectory => Path.Combine(_root, ".orchestrator");
        public string OutputDirectory => Path.Combine(StateDirectory, "reports");
        public string CheckoutParent => Path.Combine(_root, "checkouts");

        public ReplayFixture(int changedSourceCount = 1)
        {
            Git("init", "--quiet", "--template=");
            Git("config", "user.name", "Impact replay fixture");
            Git("config", "user.email", "impact-replay@example.invalid");
            Git("config", "commit.gpgsign", "false");
            Git("config", "core.autocrlf", "false");
            Git("config", "core.hooksPath", Path.Combine(_root, "no-hooks"));
            Write(".gitignore", ".orchestrator/\ncheckouts/\n");
            Write("src/Sample/Sample.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
            Write("src/Sample/Alpha.cs", "namespace Sample; public class Alpha { public int Value => 1; }");
            Write("src/Sample/Beta.cs", "namespace Sample; public class Beta { public int Value => 1; }");
            Write(TestProject + "/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <ItemGroup><ProjectReference Include="../../src/Sample/Sample.csproj" /></ItemGroup>
                </Project>
                """);
            Write(TestProject + "/AlphaTests.cs", """
                namespace SampleTests;
                public class AlphaTests { [Xunit.Fact] public void Works() { var subject = new Sample.Alpha(); } }
                """);
            Write(TestProject + "/BetaTests.cs", """
                namespace SampleTests;
                public class BetaTests { [Xunit.Fact] public void Works() { var subject = new Sample.Beta(); } }
                """);
            Write(TestProject + "/FloorTests.cs", """
                namespace SampleTests;
                public class FloorTests { [Xunit.Fact] public void Works() { } }
                """);
            Write("config/acceptance-manifest.json", """
                {
                  "version": 1,
                  "engine": {
                    "laneTestSourceRoots": ["tests/Mcg.AgentOrchestrator.Infrastructure.Tests"],
                    "infrastructureTestLanes": [
                      { "name": "Alpha lane", "filter": "FullyQualifiedName~AlphaTests", "estimatedSerialSeconds": 11 },
                      { "name": "Beta lane", "filter": "FullyQualifiedName~BetaTests", "estimatedSerialSeconds": 23.5 },
                      { "name": "Chaos gate", "filter": "FullyQualifiedName~FloorTests", "estimatedSerialSeconds": 7 }
                    ]
                  },
                  "checks": [{ "name": "Infrastructure tests", "type": "dotnet-test",
                    "project": "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj" }]
                }
                """);
            Commit();
            _main = Git("rev-parse", "HEAD").Trim();
            Write("src/Sample/Alpha.cs", "namespace Sample; public class Alpha { public int Value => 2; }");
            for (var i = 1; i < changedSourceCount; i++)
                Write($"src/Sample/Extra{i}.cs", $"namespace Sample; public class Extra{i} {{ }}");
            Commit();
            _branch = Git("rev-parse", "HEAD").Trim();
            Assert.NotEqual(_main, _branch);
            // Leave the execution checkout at main, so successful selection must read the historical worktree.
            Git("checkout", "--quiet", "--detach", _main);
        }

        public void AddAttempt(string id, bool failingBeta = false)
        {
            var directory = ".orchestrator/acceptance-gate-attempts/fixture-goal/";
            var trxPaths = new List<string>();
            foreach (var (laneSlug, className, failed) in new[]
                     { ("alpha-lane", "AlphaTests", false), ("beta-lane", "BetaTests", failingBeta), ("chaos-gate", "FloorTests", false) })
            {
                var name = $"{id}.infrastructure-tests-{laneSlug}.trx";
                trxPaths.Add(name);
                Write(directory + name, $$"""
                    <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
                      <TestDefinitions><UnitTest id="test-1" name="Works">
                        <TestMethod className="SampleTests.{{className}}" name="Works" />
                      </UnitTest></TestDefinitions>
                      <Results><UnitTestResult testId="test-1" testName="SampleTests.{{className}}.Works"
                        outcome="{{(failed ? "Failed" : "Passed")}}" /></Results>
                    </TestRun>
                    """);
            }
            Write(directory + id + ".attempt.json", JsonSerializer.Serialize(new
            {
                attemptId = id, goalId = "fixture-goal", kind = "gate",
                startedAt = new DateTimeOffset(2026, 10, 28, 12, 0, 0, TimeSpan.Zero),
                branchHeadSha = _branch, mainHeadSha = _main, testResultPaths = trxPaths
            }, Json));
            Write(directory + id + ".result.json", JsonSerializer.Serialize(new
            {
                kind = "completed", branchHeadSha = _branch, mainHeadSha = _main
            }, Json));
        }

        public (ImpactReplayReport Report, string Markdown) Run()
        {
            using var output = new StringWriter();
            using var error = new StringWriter();
            var exit = CliImpactReplayCommand.Run(
                ["impact-replay", "--since", "2026-10-01", "--limit", "100", "--out", OutputDirectory],
                _root, StateDirectory, CheckoutParent, output, error);
            Assert.True(exit == 0, $"exit={exit}; stdout={output}; stderr={error}");
            Assert.Equal("", error.ToString());
            var report = JsonSerializer.Deserialize<ImpactReplayReport>(
                File.ReadAllText(Path.Combine(OutputDirectory, "impact-replay.json")), Json);
            Assert.NotNull(report);
            Assert.Empty(report.SkippedInputs);
            Assert.Empty(report.UnreplayableAttempts);
            Assert.Empty(report.CleanupErrors);
            return (report, File.ReadAllText(Path.Combine(OutputDirectory, "impact-replay.md")));
        }

        public string[] StateSnapshot() => Directory.EnumerateFileSystemEntries(StateDirectory, "*", SearchOption.AllDirectories)
            .Where(path => path != OutputDirectory && !path.StartsWith(OutputDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            .Select(path => Path.GetRelativePath(StateDirectory, path) + ":" +
                (File.Exists(path) ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) : "directory"))
            .Order(StringComparer.Ordinal).ToArray();

        public void Write(string relativePath, string text)
        {
            var path = Path.Combine(_root, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text);
        }

        private void Commit()
        {
            Git("add", ".");
            Git("commit", "--quiet", "-m", "fixture");
        }

        public string Git(params string[] args)
        {
            var start = new ProcessStartInfo("git")
            {
                WorkingDirectory = _root, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            start.Environment.Clear();
            foreach (var name in new[] { "PATH", "SystemRoot", "WINDIR", "TEMP", "TMP", "PATHEXT" })
                if (Environment.GetEnvironmentVariable(name) is { } value) start.Environment[name] = value;
            start.Environment["HOME"] = _root;
            start.Environment["USERPROFILE"] = _root;
            start.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
            start.Environment["GIT_CONFIG_GLOBAL"] = Path.Combine(_root, "no-global-config");
            start.Environment["GIT_TERMINAL_PROMPT"] = "0";
            foreach (var arg in args) start.ArgumentList.Add(arg);
            using var process = Process.Start(start);
            Assert.NotNull(process);
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(60_000))
            {
                process.Kill(entireProcessTree: true);
                Assert.Fail("Fixture Git command did not exit within the hang-detector bound.");
            }
            Assert.True(process.ExitCode == 0, $"git {string.Join(' ', args)}: {stderr.GetAwaiter().GetResult()}");
            return stdout.GetAwaiter().GetResult();
        }

        public void Dispose()
        {
            if (!Directory.Exists(_root)) return;
            // Git objects may be read-only on Windows; only normalize files owned by this fixture.
            foreach (var path in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
            {
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReadOnly) != 0)
                    File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
            }
            Directory.Delete(_root, recursive: true);
        }
    }
}
