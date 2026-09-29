using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

[Xunit.Collection("IsolatedProcessSpawning")]
public sealed class AcceptanceFailureCensusScriptTests
{
    [Xunit.Fact]
    public void FailureCorpus_AggregatesAndOrdersSignatures()
    {
        using var fixture = CensusFixture.Create();
        fixture.WriteAttempt();
        fixture.WriteTrx(
            "alpha",
            new Result("AlphaTest", "Failed", "Assert.True() Failure", "at Alpha.Run() in C:\\src\\Alpha.cs:line 41"),
            new Result("BetaTest", "Failed", "Assert.True() Failure", "at Alpha.Run() in C:\\src\\Alpha.cs:line 41"),
            new Result("DifferentFrameTest", "Failed", "Assert.True() Failure", "at Other.Run() in C:\\src\\Other.cs:line 17"),
            new Result("PassingTest", "Passed"));
        fixture.WriteTrx(
            "beta",
            new Result("DifferentMessageTest", "Failed", "Expected: 2 Actual: 1", "at Alpha.Run() in C:\\src\\Alpha.cs:line 41"),
            new Result("TimedOutTest", "Timeout"));
        fixture.WriteJournal("core tests", "infrastructure tests");

        var result = fixture.Run("-Goal", fixture.GoalId[..8]);

        Xunit.Assert.True(result.ExitCode == 0, result.Stdout + result.Stderr);
        Xunit.Assert.Contains("status=ok", result.Stdout, StringComparison.Ordinal);
        Xunit.Assert.Contains("failedChecks=core tests,infrastructure tests", result.Stdout, StringComparison.Ordinal);
        Xunit.Assert.Contains("trx=2 executed~6 failed=4 distinctSignatures=3", result.Stdout, StringComparison.Ordinal);
        Xunit.Assert.Contains("unreadable=0 unparseable=0 otherNonPassing=1 signaturesSuppressed=0", result.Stdout, StringComparison.Ordinal);
        var frequent = result.Stdout.IndexOf("[1] count=2 (50.0%)", StringComparison.Ordinal);
        var minority = result.Stdout.IndexOf("[2] count=1 (25.0%)", StringComparison.Ordinal);
        Xunit.Assert.True(frequent >= 0 && minority > frequent, result.Stdout);
        Xunit.Assert.Equal(2, CountOccurrences(result.Stdout, "message=Assert.True() Failure"));
        Xunit.Assert.Equal(2, CountOccurrences(result.Stdout, "frame=at Alpha.Run() in C:\\src\\Alpha.cs:line 41"));
        Xunit.Assert.Contains("message=Expected: 2 Actual: 1", result.Stdout, StringComparison.Ordinal);
        Xunit.Assert.Contains("frame=at Other.Run() in C:\\src\\Other.cs:line 17", result.Stdout, StringComparison.Ordinal);
        Xunit.Assert.Contains("source=C:\\src\\Alpha.cs:41", result.Stdout, StringComparison.Ordinal);
        Xunit.Assert.Contains("example=AlphaTest", result.Stdout, StringComparison.Ordinal);
        Xunit.Assert.Contains("example=DifferentFrameTest", result.Stdout, StringComparison.Ordinal);
        Xunit.Assert.Contains("example=DifferentMessageTest", result.Stdout, StringComparison.Ordinal);
        Xunit.Assert.True(string.IsNullOrWhiteSpace(result.Stderr), result.Stderr);
    }

    [Xunit.Fact]
    public void PassingCorpus_ReportsExplicitZeroByAttempt()
    {
        using var fixture = CensusFixture.Create();
        fixture.WriteAttempt();
        fixture.WriteTrx("passing", new Result("PassingTest", "Passed"));

        var result = fixture.Run("-Attempt", fixture.AttemptId[..8]);

        Xunit.Assert.True(result.ExitCode == 0, result.Stdout + result.Stderr);
        Xunit.Assert.Contains("trx=1 executed~1 failed=0 distinctSignatures=0", result.Stdout, StringComparison.Ordinal);
        Xunit.Assert.Contains("No failing test results in this attempt corpus.", result.Stdout, StringComparison.Ordinal);
        Xunit.Assert.True(string.IsNullOrWhiteSpace(result.Stderr), result.Stderr);
    }

    [Xunit.Fact]
    public void MissingRoot_IsNonCrashingAndClear()
    {
        using var fixture = CensusFixture.Create();
        Directory.Delete(fixture.AttemptsRoot, recursive: true);

        var result = fixture.Run("-Goal", "missing");

        Xunit.Assert.Equal(0, result.ExitCode);
        Xunit.Assert.Contains("status=missing", result.Stdout, StringComparison.Ordinal);
        Xunit.Assert.Contains("attempts root not found", result.Stdout, StringComparison.Ordinal);
        Xunit.Assert.Contains("trx=0 executed~0 failed=unknown distinctSignatures=unknown", result.Stdout, StringComparison.Ordinal);
        Xunit.Assert.True(string.IsNullOrWhiteSpace(result.Stderr), result.Stderr);
    }

    [Xunit.Fact]
    public void PartialCorpus_ContinuesPastIncompleteFiles()
    {
        using var fixture = CensusFixture.Create();
        fixture.WriteAttempt();
        fixture.WriteTrx("complete", new Result("PassingTest", "Passed"));
        fixture.WriteRawTrx("truncated", "<TestRun><Results>");
        fixture.WriteRawTrx("empty", string.Empty);

        var result = fixture.Run("-Goal", fixture.GoalId);

        Xunit.Assert.Equal(0, result.ExitCode);
        Xunit.Assert.Contains("status=partial", result.Stdout, StringComparison.Ordinal);
        Xunit.Assert.Contains($"diagnostic=unparseable file={fixture.AttemptId}.empty.trx", result.Stdout, StringComparison.Ordinal);
        Xunit.Assert.Contains($"diagnostic=unparseable file={fixture.AttemptId}.truncated.trx", result.Stdout, StringComparison.Ordinal);
        Xunit.Assert.Contains("trx=3 executed~1 failed>=0 distinctSignatures>=0", result.Stdout, StringComparison.Ordinal);
        Xunit.Assert.DoesNotContain("failed=0 distinctSignatures=0", result.Stdout, StringComparison.Ordinal);
        Xunit.Assert.Contains("unreadable=0 unparseable=2", result.Stdout, StringComparison.Ordinal);
        Xunit.Assert.DoesNotContain("No failing test results in this attempt corpus.", result.Stdout, StringComparison.Ordinal);
        Xunit.Assert.Contains("No failing test results found in readable TRX files; attempt corpus is partial.", result.Stdout, StringComparison.Ordinal);
        Xunit.Assert.DoesNotContain("Exception:", result.Stderr, StringComparison.OrdinalIgnoreCase);
    }

    [Xunit.Fact]
    public void ArmFiles_AreIncludedWithoutCreatingPhantomAttempts()
    {
        using var fixture = CensusFixture.Create();
        fixture.WriteAttempt();
        fixture.WriteTrx("base", new Result("BaseTest", "Passed"));
        fixture.WriteArmTrx("candidate", "focused", new Result("CandidateTest", "Failed", "candidate failure"));
        fixture.WriteArmTrx("baseline", "focused", new Result("BaselineTest", "Failed", "baseline failure"));

        var result = fixture.Run("-Goal", fixture.GoalId);

        Xunit.Assert.Equal(0, result.ExitCode);
        Xunit.Assert.Contains($"attempt={fixture.AttemptId}", result.Stdout, StringComparison.Ordinal);
        Xunit.Assert.Contains("trx=3 executed~3 failed=2 distinctSignatures=2", result.Stdout, StringComparison.Ordinal);
        Xunit.Assert.Contains("status=ok", result.Stdout, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void InFlightMetadata_ReportsPartialWhileReadingTrx()
    {
        using var fixture = CensusFixture.Create();
        fixture.WriteRawAttempt("{\"attemptId\":");
        fixture.WriteTrx("passing", new Result("PassingTest", "Passed"));

        var result = fixture.Run("-Goal", fixture.GoalId);

        Xunit.Assert.Equal(0, result.ExitCode);
        Xunit.Assert.Contains("status=partial", result.Stdout, StringComparison.Ordinal);
        Xunit.Assert.Contains($"diagnostic=metadata-unparseable file={fixture.AttemptId}.attempt.json", result.Stdout, StringComparison.Ordinal);
        Xunit.Assert.Contains("trx=1 executed~1 failed>=0 distinctSignatures>=0", result.Stdout, StringComparison.Ordinal);
        Xunit.Assert.DoesNotContain("failed=0 distinctSignatures=0", result.Stdout, StringComparison.Ordinal);
        Xunit.Assert.DoesNotContain("No failing test results in this attempt corpus.", result.Stdout, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void OutputContract_ReportsCapMismatchAndUnavailableFrame()
    {
        using var fixture = CensusFixture.Create();
        fixture.WriteAttempt();
        fixture.WriteTrxWithFailedCounter(
            "contract",
            9,
            new Result("MessageOnlyOne", "Failed", "message only"),
            new Result("MessageOnlyTwo", "Failed", "message only"),
            new Result("Framed", "Failed", "framed", "at Framed.Run() in C:\\src\\Framed.cs:line 5"));

        var result = fixture.Run("-Goal", fixture.GoalId, "-Top", "1");

        Xunit.Assert.Equal(0, result.ExitCode);
        Xunit.Assert.Contains("failed=3 distinctSignatures=2", result.Stdout, StringComparison.Ordinal);
        Xunit.Assert.Contains("signaturesSuppressed=1", result.Stdout, StringComparison.Ordinal);
        Xunit.Assert.Contains("warning=counters-mismatch parsed=3 counters=9", result.Stdout, StringComparison.Ordinal);
        Xunit.Assert.Contains("message=message only", result.Stdout, StringComparison.Ordinal);
        Xunit.Assert.Contains("frame=unavailable", result.Stdout, StringComparison.Ordinal);
        Xunit.Assert.Contains("source=unavailable", result.Stdout, StringComparison.Ordinal);
        Xunit.Assert.DoesNotContain("[2]", result.Stdout, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void InvalidSelectors_ExitTwoWithClearMessages()
    {
        using var fixture = CensusFixture.Create();
        fixture.WriteAttempt();

        var missing = fixture.Run();
        var both = fixture.Run("-Goal", fixture.GoalId, "-Attempt", fixture.AttemptId);
        fixture.CreateGoalDirectory("aaaaaaaa99999999bbbbbbbb88888888");
        var ambiguous = fixture.Run("-Goal", "aaaa");

        Xunit.Assert.Equal(2, missing.ExitCode);
        Xunit.Assert.Contains("specify exactly one", missing.Stderr, StringComparison.Ordinal);
        Xunit.Assert.Equal(2, both.ExitCode);
        Xunit.Assert.Contains("specify exactly one", both.Stderr, StringComparison.Ordinal);
        Xunit.Assert.Equal(2, ambiguous.ExitCode);
        Xunit.Assert.Contains("is ambiguous", ambiguous.Stderr, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void Reader_DoesNotMutateArtifactsOrInvokeRunner()
    {
        using var fixture = CensusFixture.Create();
        fixture.WriteAttempt();
        fixture.WriteTrx("passing", new Result("PassingTest", "Passed"));
        var before = fixture.SnapshotArtifacts();
        var poisonDirectory = Path.Combine(fixture.Root, "poison");
        Directory.CreateDirectory(poisonDirectory);
        var sentinel = Path.Combine(fixture.Root, "runner-invoked.txt");
        File.WriteAllText(
            Path.Combine(poisonDirectory, "dotnet.cmd"),
            $"@echo invoked>{sentinel}{Environment.NewLine}@exit /b 1{Environment.NewLine}");

        var result = fixture.RunWithPath(poisonDirectory, "-Goal", fixture.GoalId);

        Xunit.Assert.True(result.ExitCode == 0, result.Stdout + result.Stderr);
        Xunit.Assert.False(File.Exists(sentinel));
        Xunit.Assert.Equal(before, fixture.SnapshotArtifacts());
        var script = File.ReadAllText(fixture.ScriptPath);
        string[] forbiddenTokens =
        [
            "dotnet", "Invoke-TestSummary", "Invoke-IsolatedDotnet", "MtpTestRunner", "Start-Process",
            "Invoke-Expression", "Set-Content", "Add-Content", "Out-File", "New-Item", "Remove-Item",
            "Move-Item", "Copy-Item", "Rename-Item", "Clear-Content"
        ];
        Xunit.Assert.All(forbiddenTokens, token =>
            Xunit.Assert.DoesNotContain(token, script, StringComparison.OrdinalIgnoreCase));
    }

    private sealed class CensusFixture : IDisposable
    {
        private CensusFixture(string root, string scriptPath)
        {
            Root = root;
            ScriptPath = scriptPath;
            AttemptsRoot = Path.Combine(root, "acceptance-gate-attempts");
            JournalRoot = Path.Combine(root, "goal-operations");
            GoalDirectory = Path.Combine(AttemptsRoot, GoalId);
            Directory.CreateDirectory(GoalDirectory);
            Directory.CreateDirectory(JournalRoot);
        }

        public string Root { get; }
        public string ScriptPath { get; }
        public string AttemptsRoot { get; }
        public string JournalRoot { get; }
        public string GoalDirectory { get; }
        public string GoalId { get; } = "aaaaaaaa11111111bbbbbbbb22222222";
        public string AttemptId { get; } = "cccccccc33333333dddddddd44444444";

        public static CensusFixture Create()
        {
            var root = Path.Combine(Path.GetTempPath(), "mcg-acceptance-census-tests", Guid.NewGuid().ToString("N"));
            var scriptPath = Path.Combine(
                InfrastructureTestSupport.FindRepositoryRoot(),
                "scripts",
                "Get-AcceptanceFailureCensus.ps1");
            return new CensusFixture(root, scriptPath);
        }

        public void WriteAttempt()
        {
            var metadata = new
            {
                attemptId = AttemptId,
                startedAt = "2026-08-24T02:00:00Z",
                ordinal = 3
            };
            File.WriteAllText(
                Path.Combine(GoalDirectory, $"{AttemptId}.attempt.json"),
                JsonSerializer.Serialize(metadata));
        }

        public void WriteRawAttempt(string content)
            => File.WriteAllText(Path.Combine(GoalDirectory, $"{AttemptId}.attempt.json"), content);

        public void CreateGoalDirectory(string goalId)
            => Directory.CreateDirectory(Path.Combine(AttemptsRoot, goalId));

        public void WriteJournal(params string[] failedCheckNames)
        {
            var path = Path.Combine(JournalRoot, $"{GoalId}.jsonl");
            File.WriteAllText(path, JsonSerializer.Serialize(new { failedCheckNames }) + Environment.NewLine + "{truncated");
        }

        public void WriteTrx(string slug, params Result[] results)
            => WriteTrxForPrefix(AttemptId, slug, results.Count(result => result.Outcome == "Failed"), results);

        public void WriteArmTrx(string arm, string slug, params Result[] results)
            => WriteTrxForPrefix($"{AttemptId}-{arm}", slug, results.Count(result => result.Outcome == "Failed"), results);

        public void WriteTrxWithFailedCounter(string slug, int failedCounter, params Result[] results)
            => WriteTrxForPrefix(AttemptId, slug, failedCounter, results);

        private void WriteTrxForPrefix(string prefix, string slug, int failedCounter, params Result[] results)
        {
            var document = new XDocument(
                new XElement("TestRun",
                    new XElement("Results",
                        results.Select(result =>
                            new XElement("UnitTestResult",
                                new XAttribute("testName", result.Name),
                                new XAttribute("outcome", result.Outcome),
                                result.Message is null && result.StackTrace is null
                                    ? null
                                    : new XElement("Output",
                                        new XElement("ErrorInfo",
                                            result.Message is null ? null : new XElement("Message", result.Message),
                                            result.StackTrace is null ? null : new XElement("StackTrace", result.StackTrace)))))),
                    new XElement("ResultSummary",
                            new XElement("Counters",
                                new XAttribute("executed", results.Length),
                                new XAttribute("failed", failedCounter)))));
            document.Save(Path.Combine(GoalDirectory, $"{prefix}.{slug}.trx"));
        }

        public void WriteRawTrx(string slug, string content)
            => File.WriteAllText(Path.Combine(GoalDirectory, $"{AttemptId}.{slug}.trx"), content);

        public ProcessResult Run(params string[] selectorArguments)
            => RunCore(null, selectorArguments);

        public ProcessResult RunWithPath(string pathPrefix, params string[] selectorArguments)
            => RunCore(pathPrefix, selectorArguments);

        public string SnapshotArtifacts()
        {
            var roots = new[]
            {
                (Name: "attempts", Path: AttemptsRoot),
                (Name: "journals", Path: JournalRoot)
            };
            var lines = roots.SelectMany(root =>
                    Directory.EnumerateFiles(root.Path, "*", SearchOption.AllDirectories)
                        .Select(path => (root.Name, root.Path, FilePath: path)))
                .OrderBy(item => item.FilePath, StringComparer.Ordinal)
                .Select(item =>
                {
                    var relative = Path.GetRelativePath(item.Path, item.FilePath);
                    var bytes = File.ReadAllBytes(item.FilePath);
                    return $"{item.Name}/{relative}|{bytes.Length}|{Convert.ToHexString(SHA256.HashData(bytes))}";
                });
            return string.Join(Environment.NewLine, lines);
        }

        private ProcessResult RunCore(string? pathPrefix, IReadOnlyList<string> selectorArguments)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "pwsh",
                WorkingDirectory = Root,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-File");
            startInfo.ArgumentList.Add(ScriptPath);
            startInfo.ArgumentList.Add("-AttemptsRoot");
            startInfo.ArgumentList.Add(AttemptsRoot);
            startInfo.ArgumentList.Add("-JournalRoot");
            startInfo.ArgumentList.Add(JournalRoot);
            foreach (var argument in selectorArguments) startInfo.ArgumentList.Add(argument);

            var path = Environment.GetEnvironmentVariable("PATH");
            var systemRoot = Environment.GetEnvironmentVariable("SYSTEMROOT");
            var temp = Path.GetTempPath();
            startInfo.Environment.Clear();
            if (!string.IsNullOrWhiteSpace(path))
            {
                startInfo.Environment["PATH"] = pathPrefix is null
                    ? path
                    : pathPrefix + Path.PathSeparator + path;
            }
            if (!string.IsNullOrWhiteSpace(systemRoot))
            {
                startInfo.Environment["SYSTEMROOT"] = systemRoot;
                startInfo.Environment["WINDIR"] = systemRoot;
            }
            startInfo.Environment["TEMP"] = temp;
            startInfo.Environment["TMP"] = temp;

            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Failed to launch PowerShell fixture.");
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            ScriptFixtureProcessRunner.AssertExited(process, "PowerShell fixture exceeded its 30-second failsafe.");
            return new ProcessResult(
                process.ExitCode,
                stdout.GetAwaiter().GetResult(),
                stderr.GetAwaiter().GetResult());
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private sealed record Result(
        string Name,
        string Outcome,
        string? Message = null,
        string? StackTrace = null);

    private static int CountOccurrences(string value, string search)
    {
        var count = 0;
        var offset = 0;
        while ((offset = value.IndexOf(search, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += search.Length;
        }
        return count;
    }

    private sealed record ProcessResult(int ExitCode, string Stdout, string Stderr);
}
