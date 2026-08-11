using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.VisualBasic.FileIO;

[Xunit.Collection(TestCollections.ProcessSpawning)]
public sealed class ExcessWorkerRoundAnalysisScriptTests
{
    [Xunit.Fact(DisplayName = "Excess_round_analyzer_is_bounded_deterministic_and_preserves_missing_data")]
    public async Task ExcessRoundAnalyzerIsBoundedDeterministicAndPreservesMissingData()
    {
        using var fixture = await AnalysisFixture.CreateAsync();

        var first = fixture.Run("first");
        var second = fixture.Run("second");

        Xunit.Assert.True(first.ExitCode == 0, first.Stdout + first.Stderr);
        Xunit.Assert.True(second.ExitCode == 0, second.Stdout + second.Stderr);
        Xunit.Assert.Equal(
            File.ReadAllBytes(Path.Combine(fixture.Root, "first", "excess-worker-rounds.csv")),
            File.ReadAllBytes(Path.Combine(fixture.Root, "second", "excess-worker-rounds.csv")));
        Xunit.Assert.Equal(
            File.ReadAllBytes(Path.Combine(fixture.Root, "first", "excess-worker-rounds-summary.json")),
            File.ReadAllBytes(Path.Combine(fixture.Root, "second", "excess-worker-rounds-summary.json")));

        var rows = ReadCsv(Path.Combine(fixture.Root, "first", "excess-worker-rounds.csv"));
        Xunit.Assert.Equal(2, rows.Count);
        var sunday = Xunit.Assert.Single(rows, row => row["goal"].StartsWith("aaaa1111", StringComparison.Ordinal));
        Xunit.Assert.Equal("2026-W27", sunday["activationWeek"]);
        Xunit.Assert.Equal("3", sunday["roleRounds"]);
        Xunit.Assert.Equal("2", sunday["distinctRoleCount"]);
        Xunit.Assert.Equal("1", sunday["excessRounds"]);
        Xunit.Assert.Equal("Developer=2;Reviewer=1", sunday["roleRoundCounts"]);
        Xunit.Assert.Equal("OpenAI/gpt-fixture", sunday["providerModelsByRole"].Split(';')[0].Split('=')[1]);
        Xunit.Assert.Equal("2", sunday["criterionCount"]);
        Xunit.Assert.Equal("Planner;Reviewer", sunday["criterionOwners"]);

        var monday = Xunit.Assert.Single(rows, row => row["goal"].StartsWith("cccc3333", StringComparison.Ordinal));
        Xunit.Assert.Equal("2026-W28", monday["activationWeek"]);
        Xunit.Assert.Equal("0", monday["excessRounds"]);
        Xunit.Assert.Equal("historical-task-snapshot-not-supplied", monday["metadataMissingReason"]);

        using var summary = JsonDocument.Parse(File.ReadAllText(Path.Combine(fixture.Root, "first", "excess-worker-rounds-summary.json")));
        Xunit.Assert.Equal(3, summary.RootElement.GetProperty("generatedFrom").GetProperty("manifestCount").GetInt32());
        Xunit.Assert.Equal(1, summary.RootElement.GetProperty("exclusions").GetProperty("missingJournal").GetInt32());
        Xunit.Assert.Equal(2, summary.RootElement.GetProperty("weekly").GetArrayLength());
    }

    [Xunit.Fact(DisplayName = "Excess_round_analyzer_fails_closed_when_a_manifest_hash_changes")]
    public async Task ExcessRoundAnalyzerFailsClosedWhenAManifestHashChanges()
    {
        using var fixture = await AnalysisFixture.CreateAsync();
        File.AppendAllText(fixture.FirstLogPath, "tampered");

        var result = fixture.Run("tampered");

        Xunit.Assert.NotEqual(0, result.ExitCode);
        Xunit.Assert.Contains("Manifest hash mismatch", result.Stderr, StringComparison.Ordinal);
        Xunit.Assert.False(File.Exists(Path.Combine(fixture.Root, "tampered", "excess-worker-rounds.csv")));
    }

    private static List<Dictionary<string, string>> ReadCsv(string path)
    {
        using var parser = new TextFieldParser(path);
        parser.SetDelimiters(",");
        parser.HasFieldsEnclosedInQuotes = true;
        var headers = parser.ReadFields() ?? throw new InvalidOperationException("CSV has no header.");
        var rows = new List<Dictionary<string, string>>();
        while (!parser.EndOfData)
        {
            var fields = parser.ReadFields() ?? [];
            rows.Add(headers.Zip(fields).ToDictionary(pair => pair.First, pair => pair.Second, StringComparer.Ordinal));
        }
        return rows;
    }

    private sealed class AnalysisFixture : IDisposable
    {
        private readonly string _script;
        private readonly string _manifest;
        private readonly string _journals;
        private readonly string _dogfood;
        private readonly string _repository;
        private readonly string _metadata;

        private AnalysisFixture(
            string root,
            string script,
            string manifest,
            string journals,
            string dogfood,
            string repository,
            string metadata,
            string firstLogPath)
        {
            Root = root;
            _script = script;
            _manifest = manifest;
            _journals = journals;
            _dogfood = dogfood;
            _repository = repository;
            _metadata = metadata;
            FirstLogPath = firstLogPath;
        }

        public string Root { get; }
        public string FirstLogPath { get; }

        public static async Task<AnalysisFixture> CreateAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), "mcg-excess-round-tests", Guid.NewGuid().ToString("N"));
            var logs = Directory.CreateDirectory(Path.Combine(root, "logs")).FullName;
            var journals = Directory.CreateDirectory(Path.Combine(root, "journals")).FullName;
            var repository = Directory.CreateDirectory(Path.Combine(root, "repo")).FullName;
            var firstLog = Path.Combine(logs, "operator-a.out.log");
            var secondLog = Path.Combine(logs, "operator-b.out.log");
            var thirdLog = Path.Combine(logs, "operator-c.out.log");
            File.WriteAllText(firstLog, "WATCH_TRANSITION goal=aaaa1111 Developer=ok commit=111 files=1 elapsed=1s next=Reviewer task=1/2\n");
            File.WriteAllText(secondLog,
                "WATCH_TRANSITION goal=aaaa1111 Reviewer=ok commit=222 files=2 elapsed=1s next=Developer task=2/2\n" +
                "WATCH_TRANSITION goal=aaaa1111 Developer=ok commit=333 files=2 elapsed=1s next=none task=1/2\n" +
                "WATCH_TRANSITION goal=bbbb2222 Developer=ok commit=444 files=0 elapsed=1s next=none task=1/1\n");
            File.WriteAllText(thirdLog, "WATCH_TRANSITION goal=cccc3333 Developer=ok commit=555 files=0 elapsed=1s next=none task=1/1\n");

            File.WriteAllText(Path.Combine(journals, "aaaa1111000000000000000000000000.jsonl"),
                "{\"operation\":\"conductor:dispatch\",\"status\":\"Begin\",\"at\":\"2026-07-06T04:30:00+00:00\"}\n" +
                "{\"operation\":\"conductor:acceptance\",\"status\":\"Begin\",\"at\":\"2026-07-06T04:31:00+00:00\"}\n");
            File.WriteAllText(Path.Combine(journals, "cccc3333000000000000000000000000.jsonl"),
                "{\"operation\":\"conductor:dispatch\",\"status\":\"Begin\",\"at\":\"2026-07-06T05:30:00+00:00\"}\n");

            var dogfood = Path.Combine(root, "dogfood.db");
            var store = new DogfoodLogStore(dogfood);
            await store.UpsertAsync(new DogfoodLogAppend(
                "aaaa1111000000000000000000000000",
                "fixture",
                "Developer task via OpenAI/gpt-fixture (exit 0, commit 333). Reviewer task via Anthropic/sonnet-fixture (exit 0, commit 333).",
                "pass",
                "Model fit: fixture",
                "fixture"));

            RunChecked(repository, "git", "init", "-b", "main");
            RunChecked(repository, "git", "config", "user.email", "fixture@example.invalid");
            RunChecked(repository, "git", "config", "user.name", "Fixture");
            File.WriteAllText(Path.Combine(repository, "fixture.txt"), "initial");
            RunChecked(repository, "git", "add", "fixture.txt");
            RunChecked(repository, "git", "commit", "-m", "Initial fixture");
            File.AppendAllText(Path.Combine(repository, "fixture.txt"), "landed");
            RunChecked(repository, "git", "add", "fixture.txt");
            RunChecked(repository, "git", "commit", "-m", "Integrate goal/aaaa1111");

            var manifest = Path.Combine(root, "manifest.csv");
            File.WriteAllLines(manifest,
            [
                "path,sha256,lastWriteUtc",
                ManifestRow(firstLog, "2026-07-06T05:00:00+00:00"),
                ManifestRow(secondLog, "2026-07-06T06:00:00+00:00"),
                ManifestRow(thirdLog, "2026-07-06T07:00:00+00:00")
            ]);
            var metadata = Path.Combine(root, "metadata.csv");
            File.WriteAllText(metadata,
                "goal,selectedPipeline,complexity,risk,criterionCount,criterionOwners,changedPathScope\n" +
                "aaaa1111000000000000000000000000,Developer>Reviewer,Complex,routed,2,\"Planner;Reviewer\",src+tests\n");

            var script = Path.Combine(InfrastructureTestSupport.FindRepositoryRoot(), "scripts", "Analyze-ExcessWorkerRounds.ps1");
            return new AnalysisFixture(root, script, manifest, journals, dogfood, repository, metadata, firstLog);
        }

        public ProcessResult Run(string outputName)
        {
            var output = Path.Combine(Root, outputName);
            return RunProcess(Root, "pwsh",
                "-NoProfile", "-File", _script,
                "-OperatorLogManifest", _manifest,
                "-JournalRoot", _journals,
                "-DogfoodDbPath", _dogfood,
                "-RepositoryRoot", _repository,
                "-StartWeek", "2026-W27",
                "-EndWeek", "2026-W28",
                "-TimeZoneId", "America/Chicago",
                "-CutoffUtc", "2026-08-07T22:43:03.4206169Z",
                "-TaskMetadataSnapshot", _metadata,
                "-OutputDirectory", output);
        }

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        private static string ManifestRow(string path, string timestamp) =>
            $"\"{path.Replace("\"", "\"\"")}\",{Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant()},{timestamp}";
    }

    private static void RunChecked(string workingDirectory, string fileName, params string[] arguments)
    {
        var result = RunProcess(workingDirectory, fileName, arguments);
        Xunit.Assert.True(result.ExitCode == 0, result.Stdout + result.Stderr);
    }

    private static ProcessResult RunProcess(string workingDirectory, string fileName, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException($"Could not start {fileName}.");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return new ProcessResult(process.ExitCode, stdout, stderr);
    }

    private sealed record ProcessResult(int ExitCode, string Stdout, string Stderr);
}
