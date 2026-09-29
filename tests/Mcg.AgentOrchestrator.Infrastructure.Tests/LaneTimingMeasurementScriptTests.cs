using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

[Xunit.Collection("IsolatedProcessSpawning")]
public sealed class LaneTimingMeasurementScriptTests
{
    [Xunit.Fact]
    public void SameWindow_ProducesByteIdenticalConsoleOutput()
    {
        using var fixture = LaneTimingFixture.Create();
        var firstJson = Path.Combine(fixture.Root, "first.json");
        var secondJson = Path.Combine(fixture.Root, "second.json");

        var first = fixture.Run("-Json", firstJson);
        var second = fixture.Run("-Json", secondJson);

        Xunit.Assert.True(first.ExitCode == 0, first.Stdout + first.Stderr);
        Xunit.Assert.True(second.ExitCode == 0, second.Stdout + second.Stderr);
        Xunit.Assert.Equal(Encoding.UTF8.GetBytes(first.Stdout), Encoding.UTF8.GetBytes(second.Stdout));
        Xunit.Assert.Equal(File.ReadAllBytes(firstJson), File.ReadAllBytes(secondJson));
        Xunit.Assert.Contains("Process mean (s)", first.Stdout, StringComparison.Ordinal);
        Xunit.Assert.Contains("Test mean (s)", first.Stdout, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void MissingManifestLane_FailsAndNamesLane()
    {
        using var fixture = LaneTimingFixture.Create();
        fixture.WriteManifestWithChecks(
            ["Alpha", "Beta", "Gamma"],
            ("git diff whitespace", "command"),
            ("core tests", "dotnet-test"),
            ("infrastructure tests", "dotnet-test"));

        var result = fixture.Run();

        Xunit.Assert.NotEqual(0, result.ExitCode);
        Xunit.Assert.Contains("Manifest lane(s) with no qualified receipts:", result.Stderr, StringComparison.Ordinal);
        Xunit.Assert.Contains("Gamma", result.Stderr, StringComparison.Ordinal);
        Xunit.Assert.Contains("core tests", result.Stderr, StringComparison.Ordinal);
        Xunit.Assert.True(string.IsNullOrWhiteSpace(result.Stdout), result.Stdout);
    }

    [Xunit.Fact]
    public void ManifestChecks_AreReconciledAndNonTestChecksAreExplicit()
    {
        using var fixture = LaneTimingFixture.Create();
        fixture.WriteManifestWithChecks(
            ["Alpha", "Beta"],
            ("git diff whitespace", "command"),
            ("core tests", "dotnet-test"),
            ("infrastructure tests", "dotnet-test"));
        fixture.AddCheckReceipt("core-a", "core tests", 50, 10, startOffsetMinutes: 4);
        fixture.AddCheckReceipt("diff-a", "git diff whitespace", 2, 0, startOffsetMinutes: 5, writeTrx: false);
        var jsonPath = Path.Combine(fixture.Root, "manifest-checks.json");

        var result = fixture.Run("-Json", jsonPath);

        Xunit.Assert.True(result.ExitCode == 0, result.Stdout + result.Stderr);
        Xunit.Assert.Contains("core tests", result.Stdout, StringComparison.Ordinal);
        Xunit.Assert.Contains("nonTimingCheck=1", result.Stdout, StringComparison.Ordinal);
        using var document = JsonDocument.Parse(File.ReadAllText(jsonPath));
        Xunit.Assert.True(document.RootElement.GetProperty("lanes").TryGetProperty("core tests", out _));
        var manifest = document.RootElement.GetProperty("manifest");
        Xunit.Assert.Contains(
            manifest.GetProperty("nonTimingChecks").EnumerateArray(),
            item => item.GetProperty("name").GetString() == "git diff whitespace");
        Xunit.Assert.Contains(
            manifest.GetProperty("expandedChecks").EnumerateArray(),
            item => item.GetString() == "infrastructure tests");
    }

    [Xunit.Fact]
    public void CheckReceipt_CommandMustNameItsTrx()
    {
        using var fixture = LaneTimingFixture.Create();
        fixture.WriteManifestWithChecks(
            ["Alpha", "Beta"],
            ("core tests", "dotnet-test"),
            ("infrastructure tests", "dotnet-test"));
        fixture.AddCheckReceipt(
            "core-build",
            "core tests",
            99,
            9,
            startOffsetMinutes: 4,
            commandLine: "dotnet build Mcg.AgentOrchestrator.Core.Tests.csproj");
        fixture.AddCheckReceipt("core-test", "core tests", 12, 3, startOffsetMinutes: 5);
        var jsonPath = Path.Combine(fixture.Root, "command-association.json");

        var result = fixture.Run("-Json", jsonPath);

        Xunit.Assert.True(result.ExitCode == 0, result.Stdout + result.Stderr);
        using var document = JsonDocument.Parse(File.ReadAllText(jsonPath));
        var core = document.RootElement.GetProperty("lanes").GetProperty("core tests");
        Xunit.Assert.Equal(1, core.GetProperty("runs").GetInt32());
        Xunit.Assert.Equal(12, core.GetProperty("processSeconds").GetProperty("min").GetDouble());
        Xunit.Assert.Equal(
            1,
            document.RootElement
                .GetProperty("qualification")
                .GetProperty("exclusionReasons")
                .GetProperty("trxCommandMismatch")
                .GetInt32());
    }

    [Xunit.Fact]
    public void ProcessAndTestStatistics_AreDistinctAndRanged()
    {
        using var fixture = LaneTimingFixture.Create();
        var jsonPath = Path.Combine(fixture.Root, "statistics.json");

        var result = fixture.Run("-Json", jsonPath);

        Xunit.Assert.True(result.ExitCode == 0, result.Stdout + result.Stderr);
        using var document = JsonDocument.Parse(File.ReadAllText(jsonPath));
        var alpha = document.RootElement.GetProperty("lanes").GetProperty("Alpha");
        var process = alpha.GetProperty("processSeconds");
        var test = alpha.GetProperty("testSeconds");
        Xunit.Assert.Equal(15, process.GetProperty("mean").GetDouble());
        Xunit.Assert.Equal(10, process.GetProperty("min").GetDouble());
        Xunit.Assert.Equal(20, process.GetProperty("max").GetDouble());
        Xunit.Assert.Equal(3, test.GetProperty("mean").GetDouble());
        Xunit.Assert.Equal(2, test.GetProperty("min").GetDouble());
        Xunit.Assert.Equal(4, test.GetProperty("max").GetDouble());
    }

    [Xunit.Fact]
    public void ExcludedReceipts_AreTalliedByReason()
    {
        using var fixture = LaneTimingFixture.Create();
        fixture.AddExclusionReceipts();

        var result = fixture.Run();

        Xunit.Assert.True(result.ExitCode == 0, result.Stdout + result.Stderr);
        Xunit.Assert.Contains("Receipts: total=9 included=4 excluded=5", result.Stdout, StringComparison.Ordinal);
        Xunit.Assert.Contains("missingAttemptId=1", result.Stdout, StringComparison.Ordinal);
        Xunit.Assert.Contains("nonCompletedState=1", result.Stdout, StringComparison.Ordinal);
        Xunit.Assert.Contains("nonZeroExitCode=1", result.Stdout, StringComparison.Ordinal);
        Xunit.Assert.Contains("unparseableTimestamps=1", result.Stdout, StringComparison.Ordinal);
        Xunit.Assert.Contains("incompleteResultCount=1", result.Stdout, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void SavedBaseline_ComparisonShowsRangesAndCommits()
    {
        using var fixture = LaneTimingFixture.Create();
        var baselinePath = Path.Combine(fixture.Root, "baseline.json");
        var jsonPath = Path.Combine(fixture.Root, "measurement.json");
        var saved = fixture.Run("-SaveBaseline", baselinePath, "-Json", jsonPath);
        Xunit.Assert.True(saved.ExitCode == 0, saved.Stdout + saved.Stderr);
        Xunit.Assert.Equal(File.ReadAllBytes(baselinePath), File.ReadAllBytes(jsonPath));

        using (var document = JsonDocument.Parse(File.ReadAllText(baselinePath)))
        {
            var root = document.RootElement;
            Xunit.Assert.Equal(fixture.SinceSha, root.GetProperty("window").GetProperty("commitSha").GetString());
            var alpha = root.GetProperty("lanes").GetProperty("Alpha");
            Xunit.Assert.Equal(2, alpha.GetProperty("runs").GetInt32());
            Xunit.Assert.Equal(2, alpha.GetProperty("samples").GetArrayLength());
            Xunit.Assert.Equal(10, alpha.GetProperty("processSeconds").GetProperty("min").GetDouble());
            Xunit.Assert.Equal(20, alpha.GetProperty("processSeconds").GetProperty("max").GetDouble());
        }

        fixture.ReplaceWithChangedReceipts();
        var compared = fixture.Run("-CompareTo", baselinePath);

        Xunit.Assert.True(compared.ExitCode == 0, compared.Stdout + compared.Stderr);
        Xunit.Assert.Contains($"Comparison: baseline {fixture.SinceSha} -> current {fixture.SinceSha}", compared.Stdout, StringComparison.Ordinal);
        Xunit.Assert.Contains("Alpha | present | 10-20 | 15-25 | 5 | 2-4 | 3-5 | 1", compared.Stdout, StringComparison.Ordinal);
        Xunit.Assert.Contains("Qualification counts: baseline included=4 excluded=0; current included=4 excluded=0", compared.Stdout, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void IncludeUnqualified_MarksConsoleAndJson()
    {
        using var fixture = LaneTimingFixture.Create();
        fixture.ReplaceWithUnqualifiedReceipts();
        var jsonPath = Path.Combine(fixture.Root, "unqualified.json");

        var result = fixture.Run("-IncludeUnqualified", "-Json", jsonPath);

        Xunit.Assert.True(result.ExitCode == 0, result.Stdout + result.Stderr);
        Xunit.Assert.All(
            result.Stdout.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries),
            line => Xunit.Assert.StartsWith("UNQUALIFIED ", line, StringComparison.Ordinal));
        using var document = JsonDocument.Parse(File.ReadAllText(jsonPath));
        Xunit.Assert.False(document.RootElement.GetProperty("qualification").GetProperty("qualified").GetBoolean());
        Xunit.Assert.Equal(2, document.RootElement.GetProperty("qualification").GetProperty("includedReceipts").GetInt32());
        Xunit.Assert.Equal(2, document.RootElement.GetProperty("qualification").GetProperty("excludedReceipts").GetInt32());
    }

    private sealed class LaneTimingFixture : IDisposable
    {
        private static readonly DateTimeOffset BaseTime = DateTimeOffset.UtcNow.AddHours(-6);
        private readonly string _scriptPath;
        private readonly string _goalDirectory;
        private string _gitExecutablePath = string.Empty;

        private LaneTimingFixture(string root, string repositoryRoot, string scriptPath)
        {
            Root = root;
            RepositoryRoot = repositoryRoot;
            _scriptPath = scriptPath;
            ReceiptsRoot = Path.Combine(root, "receipts");
            _goalDirectory = Path.Combine(ReceiptsRoot, "goal-fixture");
            ManifestPath = Path.Combine(root, "acceptance-manifest.json");
            Directory.CreateDirectory(_goalDirectory);
        }

        public string Root { get; }
        public string RepositoryRoot { get; }
        public string ReceiptsRoot { get; }
        public string ManifestPath { get; }
        public string SinceSha { get; private set; } = string.Empty;

        public static LaneTimingFixture Create()
        {
            var root = Path.Combine(Path.GetTempPath(), "mcg-lane-timing-tests", Guid.NewGuid().ToString("N"));
            var repositoryRoot = Path.Combine(root, "repository");
            Directory.CreateDirectory(repositoryRoot);
            var fixture = new LaneTimingFixture(
                root,
                repositoryRoot,
                Path.Combine(InfrastructureTestSupport.FindRepositoryRoot(), "scripts", "Measure-LaneTimings.ps1"));
            fixture.InitializeRepository();
            fixture.WriteManifest("Alpha", "Beta");
            fixture.AddStandardReceipts();
            return fixture;
        }

        public void WriteManifest(params string[] laneNames)
            => WriteManifestWithChecks(laneNames);

        public void WriteManifestWithChecks(
            string[] laneNames,
            params (string Name, string Type)[] checks)
        {
            var manifest = new
            {
                version = 1,
                engine = new
                {
                    infrastructureTestLanes = laneNames.Select(name => new { name, filter = "fixture" }).ToArray()
                },
                checks = checks.Select(check => new { name = check.Name, type = check.Type }).ToArray()
            };
            File.WriteAllText(ManifestPath, JsonSerializer.Serialize(manifest));
        }

        public void AddCheckReceipt(
            string attemptId,
            string checkName,
            double processSeconds,
            double testSeconds,
            int startOffsetMinutes = 0,
            bool writeTrx = true,
            string? commandLine = null)
            => AddReceipt(
                attemptId,
                checkName,
                processSeconds,
                testSeconds,
                startOffsetMinutes: startOffsetMinutes,
                currentTarget: checkName,
                artifactStem: Slug(checkName),
                writeTrx: writeTrx,
                commandLine: commandLine);

        public void AddExclusionReceipts()
        {
            AddReceipt("missing-attempt", "Alpha", 12, 2, writeAttempt: false);
            AddReceipt("running", "Alpha", 12, 2, state: "running");
            AddReceipt("failed-exit", "Alpha", 12, 2, exitCode: 1);
            AddReceipt("bad-time", "Alpha", 12, 2, invalidHeartbeatTime: true);
            AddReceipt("empty-shell", "Alpha", 12, 2, emptyTrx: true);
        }

        public void ReplaceWithChangedReceipts()
        {
            ResetReceipts();
            AddReceipt("alpha-a", "Alpha", 15, 3);
            AddReceipt("alpha-b", "Alpha", 25, 5, startOffsetMinutes: 1);
            AddReceipt("beta-a", "Beta", 35, 7, startOffsetMinutes: 2);
            AddReceipt("beta-b", "Beta", 45, 9, startOffsetMinutes: 3);
        }

        public void ReplaceWithUnqualifiedReceipts()
        {
            ResetReceipts();
            AddReceipt("alpha-failed", "Alpha", 15, 3, state: "failed", exitCode: 1);
            AddReceipt("beta-failed", "Beta", 25, 5, state: "failed", exitCode: 1, startOffsetMinutes: 1);
        }

        public ProcessResult Run(params string[] extraArguments)
        {
            var arguments = new List<string>
            {
                "-NoProfile",
                "-File", _scriptPath,
                "-Since", SinceSha,
                "-RepositoryRoot", RepositoryRoot,
                "-ReceiptsRoot", ReceiptsRoot,
                "-ManifestPath", ManifestPath,
                "-GitPath", _gitExecutablePath
            };
            arguments.AddRange(extraArguments);
            return RunProcess(Path.GetTempPath(), "pwsh", arguments);
        }

        private void InitializeRepository()
        {
            RunChecked(Path.GetTempPath(), "git", ["-C", RepositoryRoot, "init", "--quiet"]);
            File.WriteAllText(Path.Combine(RepositoryRoot, "fixture.txt"), "fixture");
            RunChecked(Path.GetTempPath(), "git", ["-C", RepositoryRoot, "add", "fixture.txt"]);
            var commitEnvironment = new Dictionary<string, string?>
            {
                ["GIT_AUTHOR_DATE"] = BaseTime.AddHours(-2).ToString("O", CultureInfo.InvariantCulture),
                ["GIT_COMMITTER_DATE"] = BaseTime.AddHours(-2).ToString("O", CultureInfo.InvariantCulture)
            };
            RunChecked(
                Path.GetTempPath(),
                "git",
                ["-C", RepositoryRoot, "-c", "user.name=Lane Timing Fixture", "-c", "user.email=fixture@example.invalid", "commit", "--quiet", "-m", "baseline"],
                commitEnvironment);
            SinceSha = RunChecked(Path.GetTempPath(), "git", ["-C", RepositoryRoot, "rev-parse", "HEAD"]).Stdout.Trim();
            _gitExecutablePath = Path.Combine(Root, "git-fixture.ps1");
            File.WriteAllText(
                _gitExecutablePath,
                $$"""
                param([Parameter(ValueFromRemainingArguments = $true)][string[]]$Arguments)
                $expectedSha = '{{SinceSha}}'
                if ($Arguments.Count -eq 5 -and $Arguments[0] -eq '-C' -and $Arguments[2] -eq 'rev-parse' -and $Arguments[3] -eq '--verify' -and $Arguments[4] -eq $expectedSha) {
                    Write-Output $expectedSha
                    return
                }
                if ($Arguments.Count -eq 6 -and $Arguments[0] -eq '-C' -and $Arguments[2] -eq 'show' -and $Arguments[3] -eq '-s' -and $Arguments[4] -eq '--format=%cI' -and $Arguments[5] -eq $expectedSha) {
                    Write-Output '{{BaseTime.AddHours(-2).ToString("O", CultureInfo.InvariantCulture)}}'
                    return
                }
                [Console]::Error.WriteLine('unexpected git fixture arguments: ' + ($Arguments -join ' '))
                exit 17
                """);
        }

        private void AddStandardReceipts()
        {
            AddReceipt("alpha-a", "Alpha", 10, 2);
            AddReceipt("alpha-b", "Alpha", 20, 4, startOffsetMinutes: 1);
            AddReceipt("beta-a", "Beta", 30, 6, startOffsetMinutes: 2);
            AddReceipt("beta-b", "Beta", 40, 8, startOffsetMinutes: 3);
        }

        private void ResetReceipts()
        {
            Directory.Delete(ReceiptsRoot, recursive: true);
            Directory.CreateDirectory(_goalDirectory);
        }

        private void AddReceipt(
            string attemptId,
            string lane,
            double processSeconds,
            double testSeconds,
            string state = "completed",
            int exitCode = 0,
            bool writeAttempt = true,
            bool invalidHeartbeatTime = false,
            bool emptyTrx = false,
            int startOffsetMinutes = 0,
            string? currentTarget = null,
            string? artifactStem = null,
            bool writeTrx = true,
            string? commandLine = null)
        {
            var startedAt = BaseTime.AddMinutes(startOffsetMinutes);
            var prefix = Path.Combine(_goalDirectory, attemptId);
            if (writeAttempt)
            {
                File.WriteAllText(
                    prefix + ".attempt.json",
                    JsonSerializer.Serialize(new { attemptId, startedAt = startedAt.ToString("O") }));
            }

            var slug = Slug(lane);
            artifactStem ??= $"infrastructure-tests-{slug}";
            var trxFileName = $"{attemptId}.{artifactStem}.trx";
            commandLine ??= $"fixture-host --report-trx-filename {trxFileName}";
            var heartbeat = new
            {
                currentTarget = currentTarget ?? $"infrastructure tests: {lane}",
                commandLine,
                state,
                exitCode,
                startedAt = invalidHeartbeatTime ? "not-a-time" : startedAt.ToString("O"),
                lastObservedAt = invalidHeartbeatTime ? "not-a-time" : startedAt.AddSeconds(processSeconds).ToString("O")
            };
            File.WriteAllText(
                $"{prefix}.{artifactStem}-fixture.gate-heartbeat.json",
                JsonSerializer.Serialize(heartbeat));
            if (writeTrx) WriteTrx($"{prefix}.{artifactStem}.trx", testSeconds, emptyTrx);
        }

        private static void WriteTrx(string path, double testSeconds, bool empty)
        {
            XNamespace ns = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";
            var root = new XElement(ns + "TestRun");
            if (!empty)
            {
                root.Add(
                    new XElement(
                        ns + "Results",
                        new XElement(
                            ns + "UnitTestResult",
                            new XAttribute("testName", "fixture"),
                            new XAttribute("duration", TimeSpan.FromSeconds(testSeconds).ToString("c", CultureInfo.InvariantCulture)),
                            new XAttribute("outcome", "Passed"))),
                    new XElement(
                        ns + "ResultSummary",
                        new XElement(
                            ns + "Counters",
                            new XAttribute("total", 1),
                            new XAttribute("executed", 1),
                            new XAttribute("passed", 1),
                            new XAttribute("failed", 0),
                            new XAttribute("notExecuted", 0))));
            }
            new XDocument(root).Save(path);
        }

        private static string Slug(string value)
        {
            var builder = new StringBuilder();
            var separator = false;
            foreach (var character in value.ToLowerInvariant())
            {
                if (character is >= 'a' and <= 'z' or >= '0' and <= '9')
                {
                    if (separator && builder.Length > 0) builder.Append('-');
                    builder.Append(character);
                    separator = false;
                }
                else
                {
                    separator = true;
                }
            }
            return builder.ToString();
        }

        public void Dispose()
        {
            if (!Directory.Exists(Root)) return;
            foreach (var path in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(path, FileAttributes.Normal);
            }
            Directory.Delete(Root, recursive: true);
        }

    }

    private static ProcessResult RunChecked(
        string workingDirectory,
        string executable,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string?>? environment = null)
    {
        var result = RunProcess(workingDirectory, executable, arguments, environment);
        Xunit.Assert.True(result.ExitCode == 0, result.Stdout + result.Stderr);
        return result;
    }

    private static ProcessResult RunProcess(
        string workingDirectory,
        string executable,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string?>? environment = null)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        var path = Environment.GetEnvironmentVariable("PATH");
        var systemRoot = Environment.GetEnvironmentVariable("SYSTEMROOT");
        var temp = Path.GetTempPath();
        startInfo.Environment.Clear();
        if (!string.IsNullOrWhiteSpace(path)) startInfo.Environment["PATH"] = path;
        if (!string.IsNullOrWhiteSpace(systemRoot))
        {
            startInfo.Environment["SYSTEMROOT"] = systemRoot;
            startInfo.Environment["WINDIR"] = systemRoot;
        }
        startInfo.Environment["TEMP"] = temp;
        startInfo.Environment["TMP"] = temp;
        startInfo.Environment["GIT_CONFIG_GLOBAL"] = OperatingSystem.IsWindows() ? "NUL" : "/dev/null";
        startInfo.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        if (environment is not null)
        {
            foreach (var pair in environment)
            {
                if (pair.Value is null) startInfo.Environment.Remove(pair.Key);
                else startInfo.Environment[pair.Key] = pair.Value;
            }
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Failed to start {executable}.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        ScriptFixtureProcessRunner.AssertExited(process, $"{executable} did not exit within the 30-second failsafe.");
        return new ProcessResult(
            process.ExitCode,
            stdout.GetAwaiter().GetResult(),
            stderr.GetAwaiter().GetResult());
    }

    private sealed record ProcessResult(int ExitCode, string Stdout, string Stderr);
}
