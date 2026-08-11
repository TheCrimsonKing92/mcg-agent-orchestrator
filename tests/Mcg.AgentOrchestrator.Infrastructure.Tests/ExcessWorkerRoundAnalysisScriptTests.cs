using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.VisualBasic.FileIO;

[Xunit.Collection(TestCollections.ProcessSpawning)]
public sealed class ExcessWorkerRoundAnalysisScriptTests
{
    [Xunit.Fact]
    public void FixtureProcessesUseAHermeticEnvironment()
    {
        var ambientOverrides = new Dictionary<string, string>
        {
            ["MCG_ORCHESTRATOR_REPOSITORY_ROOT"] = @"C:\ambient\wrong-repository",
            ["GIT_DIR"] = @"C:\ambient\wrong-git-dir",
            ["GIT_CONFIG_GLOBAL"] = @"C:\ambient\wrong-git-config"
        };

        var result = RunProcessWithAmbientOverrides(
            Directory.GetCurrentDirectory(),
            "pwsh",
            ambientOverrides,
            "-NoProfile",
            "-Command",
            "if ($env:MCG_ORCHESTRATOR_REPOSITORY_ROOT -or $env:GIT_DIR -or $env:GIT_CONFIG_GLOBAL -ne 'NUL') { exit 41 }; if (-not $env:PATH -or -not $env:SYSTEMROOT) { exit 42 }");

        Xunit.Assert.True(result.ExitCode == 0, result.Stdout + result.Stderr);
    }

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

    [Xunit.Fact(DisplayName = "Excess_round_analyzer_ignores_undeclared_state_and_fails_closed_on_declared_input_drift")]
    public async Task ExcessRoundAnalyzerIgnoresUndeclaredStateAndFailsClosedOnDeclaredInputDrift()
    {
        using var fixture = await AnalysisFixture.CreateAsync();
        var baseline = fixture.Run("baseline");
        Xunit.Assert.True(baseline.ExitCode == 0, baseline.Stdout + baseline.Stderr);

        File.WriteAllText(
            Path.Combine(fixture.JournalRoot, "dddd4444000000000000000000000000.jsonl"),
            "{\"operation\":\"conductor:dispatch\",\"status\":\"Begin\",\"at\":\"2026-07-06T06:00:00+00:00\"}\n");
        File.AppendAllText(Path.Combine(fixture.RepositoryRoot, "fixture.txt"), "later");
        RunChecked(fixture.RepositoryRoot, "git", "add", "fixture.txt");
        RunChecked(fixture.RepositoryRoot, "git", "commit", "-m", "Integrate goal/dddd4444");

        var ignored = fixture.Run("ignored");
        Xunit.Assert.True(ignored.ExitCode == 0, ignored.Stdout + ignored.Stderr);
        Xunit.Assert.Equal(
            File.ReadAllBytes(Path.Combine(fixture.Root, "baseline", "excess-worker-rounds-summary.json")),
            File.ReadAllBytes(Path.Combine(fixture.Root, "ignored", "excess-worker-rounds-summary.json")));

        var dogfoodBytes = File.ReadAllBytes(fixture.DogfoodPath);
        File.AppendAllText(fixture.DogfoodPath, "tampered");
        var dogfoodChanged = fixture.Run("dogfood-changed");
        Xunit.Assert.NotEqual(0, dogfoodChanged.ExitCode);
        Xunit.Assert.Contains("Dogfood database hash mismatch", dogfoodChanged.Stderr, StringComparison.Ordinal);
        Xunit.Assert.False(File.Exists(Path.Combine(fixture.Root, "dogfood-changed", "excess-worker-rounds.csv")));
        File.WriteAllBytes(fixture.DogfoodPath, dogfoodBytes);

        File.AppendAllText(fixture.FirstJournalPath, "{\"operation\":\"conductor:dispatch\",\"status\":\"End\",\"at\":\"2026-07-06T04:32:00+00:00\"}\n");
        var changed = fixture.Run("changed");
        Xunit.Assert.NotEqual(0, changed.ExitCode);
        Xunit.Assert.Contains("Journal manifest hash mismatch", changed.Stderr, StringComparison.Ordinal);
        Xunit.Assert.False(File.Exists(Path.Combine(fixture.Root, "changed", "excess-worker-rounds.csv")));
    }

    [Xunit.Fact]
    public async Task DogfoodWal_DigestBindsQueriedRows()
    {
        using var fixture = await AnalysisFixture.CreateAsync();
        var frozenDatabase = Path.Combine(fixture.Root, "dogfood-with-wal.db");
        await CreateFrozenWalInputAsync(
            fixture.DogfoodPath,
            frozenDatabase,
            "eeee5555000000000000000000000000");
        var walPath = frozenDatabase + "-wal";
        Xunit.Assert.True(new FileInfo(walPath).Length > 0, "Fixture did not materialize a non-empty SQLite WAL.");

        var databaseDigest = AnalysisFixture.FileHash(frozenDatabase);
        var unbound = fixture.Run("wal-unbound", databaseDigest, dogfoodPath: frozenDatabase);
        Xunit.Assert.NotEqual(0, unbound.ExitCode);
        Xunit.Assert.Contains("DogfoodWalSha256 was not supplied", unbound.Stderr, StringComparison.Ordinal);

        var walDigest = AnalysisFixture.FileHash(walPath);
        var bound = fixture.Run(
            "wal-bound",
            databaseDigest,
            walDigest,
            frozenDatabase);
        Xunit.Assert.True(bound.ExitCode == 0, bound.Stdout + bound.Stderr);

        var changedDatabase = Path.Combine(fixture.Root, "dogfood-with-changed-wal.db");
        await CreateFrozenWalInputAsync(
            frozenDatabase,
            changedDatabase,
            "ffff6666000000000000000000000000");
        Xunit.Assert.Equal(databaseDigest, AnalysisFixture.FileHash(changedDatabase));
        var changed = fixture.Run("wal-changed", databaseDigest, walDigest, changedDatabase);
        Xunit.Assert.NotEqual(0, changed.ExitCode);
        Xunit.Assert.Contains("Dogfood WAL hash mismatch", changed.Stderr, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public async Task ProviderTerms_WithoutTypedDispatchFailure_AreIgnored()
    {
        using var fixture = await AnalysisFixture.CreateAsync(
            firstJournalDetail: "Acceptance failed: Cli_subscription_dispatch_ready_writes_preflight_blocked_lines [FAIL]");

        var falsePositiveControl = fixture.Run("provider-false-positive");
        Xunit.Assert.True(falsePositiveControl.ExitCode == 0, falsePositiveControl.Stdout + falsePositiveControl.Stderr);
        var falsePositiveRows = ReadCsv(Path.Combine(fixture.Root, "provider-false-positive", "excess-worker-rounds.csv"));
        var falsePositive = Xunit.Assert.Single(falsePositiveRows, row => row["goal"].StartsWith("aaaa1111", StringComparison.Ordinal));
        Xunit.Assert.DoesNotContain("provider/preflight", falsePositive["failureFindingCategories"], StringComparison.Ordinal);

        File.AppendAllText(
            fixture.FirstJournalPath,
            "{\"operation\":\"conductor:dispatch\",\"status\":\"Failed\",\"at\":\"2026-07-06T04:32:00+00:00\",\"detail\":\"No tasks dispatched; assigned tasks were excluded from the ready batch: task 1 aaaaaaaa000000000000000000000000 provider=codex-cli reason=preflight-blocked: blocked: fixture\"}\n");
        fixture.RefreshFirstJournalDigest();
        var positive = fixture.Run("provider-positive");
        Xunit.Assert.True(positive.ExitCode == 0, positive.Stdout + positive.Stderr);
        var positiveRows = ReadCsv(Path.Combine(fixture.Root, "provider-positive", "excess-worker-rounds.csv"));
        var positiveRow = Xunit.Assert.Single(positiveRows, row => row["goal"].StartsWith("aaaa1111", StringComparison.Ordinal));
        Xunit.Assert.Contains("provider/preflight", positiveRow["failureFindingCategories"], StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public async Task SubscriptionPreflightReadyBlockedReason_IsProviderEvidence()
    {
        using var fixture = await AnalysisFixture.CreateAsync();
        File.AppendAllText(
            fixture.FirstJournalPath,
            "{\"operation\":\"conductor:dispatch\",\"status\":\"Failed\",\"at\":\"2026-07-06T04:32:00+00:00\",\"detail\":\"No tasks dispatched; assigned tasks were excluded from the ready batch: task 1 aaaaaaaa000000000000000000000000 provider=codex-cli reason=subscription-preflight: blocked: subscription retry is cooling down\"}\n");
        fixture.RefreshFirstJournalDigest();

        var result = fixture.Run("subscription-preflight-positive");

        Xunit.Assert.True(result.ExitCode == 0, result.Stdout + result.Stderr);
        var rows = ReadCsv(Path.Combine(fixture.Root, "subscription-preflight-positive", "excess-worker-rounds.csv"));
        var row = Xunit.Assert.Single(rows, candidate => candidate["goal"].StartsWith("aaaa1111", StringComparison.Ordinal));
        Xunit.Assert.Contains("provider/preflight", row["failureFindingCategories"], StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public async Task TaskOutcomeTokens_WithoutReadyBlockedReason_AreNotProviderEvidence()
    {
        using var fixture = await AnalysisFixture.CreateAsync();
        File.AppendAllText(
            fixture.FirstJournalPath,
            "{\"operation\":\"conductor:dispatch\",\"status\":\"Failed\",\"at\":\"2026-07-06T04:32:00+00:00\",\"detail\":\"task-outcome outcomeRule=provider-rate-limit outcome=preflight-blocked\"}\n");
        fixture.RefreshFirstJournalDigest();

        var result = fixture.Run("task-outcome-negative");

        Xunit.Assert.True(result.ExitCode == 0, result.Stdout + result.Stderr);
        var rows = ReadCsv(Path.Combine(fixture.Root, "task-outcome-negative", "excess-worker-rounds.csv"));
        var row = Xunit.Assert.Single(rows, candidate => candidate["goal"].StartsWith("aaaa1111", StringComparison.Ordinal));
        Xunit.Assert.DoesNotContain("provider/preflight", row["failureFindingCategories"], StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public async Task JournalFacts_AreCutoffBoundAndUntimestampedRowsAreIgnored()
    {
        using var fixture = await AnalysisFixture.CreateAsync();
        File.AppendAllText(
            fixture.FirstJournalPath,
            "{\"operation\":\"conductor:finding-evidence\",\"status\":\"Begin\",\"at\":\"2026-08-08T00:00:00Z\",\"detail\":\"Running focused finding evidence\"}\n" +
            "{\"operation\":\"conductor:acceptance\",\"status\":\"Begin\",\"at\":\"2026-08-08T00:00:00Z\",\"detail\":\"reason=reviewer-finding\"}\n" +
            "{\"operation\":\"conductor:dispatch\",\"status\":\"Failed\",\"detail\":\"reason=worker-result-malformed\"}\n");
        fixture.RefreshFirstJournalDigest();

        var result = fixture.Run("journal-cutoff");

        Xunit.Assert.True(result.ExitCode == 0, result.Stdout + result.Stderr);
        var rows = ReadCsv(Path.Combine(fixture.Root, "journal-cutoff", "excess-worker-rounds.csv"));
        var row = Xunit.Assert.Single(rows, candidate => candidate["goal"].StartsWith("aaaa1111", StringComparison.Ordinal));
        Xunit.Assert.Equal("0", row["focusedEvidenceRounds"]);
        Xunit.Assert.Equal("1", row["gateAttempts"]);
        Xunit.Assert.Equal(string.Empty, row["failureFindingCategories"]);
    }

    [Xunit.Fact]
    public async Task NonProviderCategories_AreWithheldWithoutJournalProducers()
    {
        using var fixture = await AnalysisFixture.CreateAsync(
            firstJournalDetail: "review finding; malformed worker; criterion impossible; no file change");

        var falsePositiveControl = fixture.Run("category-false-positive");
        Xunit.Assert.True(falsePositiveControl.ExitCode == 0, falsePositiveControl.Stdout + falsePositiveControl.Stderr);
        var falsePositiveRows = ReadCsv(Path.Combine(fixture.Root, "category-false-positive", "excess-worker-rounds.csv"));
        var falsePositive = Xunit.Assert.Single(falsePositiveRows, row => row["goal"].StartsWith("aaaa1111", StringComparison.Ordinal));
        Xunit.Assert.Equal(string.Empty, falsePositive["failureFindingCategories"]);

        File.AppendAllText(
            fixture.FirstJournalPath,
            // Production journal shapes: finding-evidence carries only the executor summary;
            // acceptance carries candidate SHAs/outcome but is not bound to a worker transition.
            "{\"operation\":\"conductor:finding-evidence\",\"status\":\"Failed\",\"at\":\"2026-07-06T04:32:00Z\",\"detail\":\"Focused evidence failed (exit 1).\"}\n" +
            "{\"operation\":\"conductor:acceptance\",\"status\":\"Failed\",\"at\":\"2026-07-06T04:33:00Z\",\"detail\":\"Acceptance failed for candidate branch=abc123 main=def456 (exit 1).\",\"branchHeadSha\":\"abc123\",\"mainHeadSha\":\"def456\",\"acceptanceOutcome\":\"failed\"}\n" +
            // Near misses use category tokens that have no production journal emitter. They must
            // remain unavailable rather than becoming invented positive evidence.
            "{\"operation\":\"conductor:finding-evidence\",\"status\":\"Failed\",\"at\":\"2026-07-06T04:34:00Z\",\"detail\":\"reason=impossible-evidence\"}\n" +
            "{\"operation\":\"conductor:dispatch-start\",\"status\":\"Failed\",\"at\":\"2026-07-06T04:35:00Z\",\"detail\":\"reason=worker-result-malformed\"}\n" +
            "{\"operation\":\"conductor:acceptance\",\"status\":\"Failed\",\"at\":\"2026-07-06T04:36:00Z\",\"detail\":\"reason=unchanged-head\"}\n" +
            "{\"operation\":\"conductor:acceptance\",\"status\":\"Failed\",\"at\":\"2026-07-06T04:37:00Z\",\"detail\":\"reason=reviewer-finding\"}\n");
        fixture.RefreshFirstJournalDigest();

        var positive = fixture.Run("category-positive");
        Xunit.Assert.True(positive.ExitCode == 0, positive.Stdout + positive.Stderr);
        var positiveRows = ReadCsv(Path.Combine(fixture.Root, "category-positive", "excess-worker-rounds.csv"));
        var positiveRow = Xunit.Assert.Single(positiveRows, row => row["goal"].StartsWith("aaaa1111", StringComparison.Ordinal));
        Xunit.Assert.Equal(string.Empty, positiveRow["failureFindingCategories"]);
        Xunit.Assert.Equal(
            "provider/preflight=journal-positive-receipt;impossible-evidence=withheld-no-journal-producer;formatting-contract=withheld-no-journal-producer;reviewer-finding=withheld-no-journal-producer;unchanged-head=withheld-no-transition-head-binding",
            positiveRow["failureFindingCategoryCoverage"]);
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
        private readonly string _manifestDigest;
        private readonly string _journals;
        private readonly string _journalManifest;
        private string _journalManifestDigest;
        private readonly string _dogfood;
        private readonly string _dogfoodDigest;
        private readonly string _repository;
        private readonly string _repositoryRevision;
        private readonly string _metadata;
        private readonly string _metadataDigest;

        private AnalysisFixture(
            string root,
            string script,
            string manifest,
            string manifestDigest,
            string journals,
            string journalManifest,
            string journalManifestDigest,
            string dogfood,
            string dogfoodDigest,
            string repository,
            string repositoryRevision,
            string metadata,
            string metadataDigest,
            string firstLogPath,
            string firstJournalPath)
        {
            Root = root;
            _script = script;
            _manifest = manifest;
            _manifestDigest = manifestDigest;
            _journals = journals;
            _journalManifest = journalManifest;
            _journalManifestDigest = journalManifestDigest;
            _dogfood = dogfood;
            _dogfoodDigest = dogfoodDigest;
            _repository = repository;
            _repositoryRevision = repositoryRevision;
            _metadata = metadata;
            _metadataDigest = metadataDigest;
            FirstLogPath = firstLogPath;
            FirstJournalPath = firstJournalPath;
        }

        public string Root { get; }
        public string FirstLogPath { get; }
        public string FirstJournalPath { get; }
        public string JournalRoot => _journals;
        public string DogfoodPath => _dogfood;
        public string RepositoryRoot => _repository;

        public static async Task<AnalysisFixture> CreateAsync(string? firstJournalDetail = null)
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

            var firstJournal = Path.Combine(journals, "aaaa1111000000000000000000000000.jsonl");
            var secondJournal = Path.Combine(journals, "cccc3333000000000000000000000000.jsonl");
            File.WriteAllText(firstJournal,
                "{\"operation\":\"conductor:dispatch\",\"status\":\"Begin\",\"at\":\"2026-07-06T04:30:00+00:00\"}\n" +
                "{\"operation\":\"conductor:acceptance\",\"status\":\"Begin\",\"at\":\"2026-07-06T04:31:00+00:00\",\"detail\":" + JsonSerializer.Serialize(firstJournalDetail) + "}\n");
            File.WriteAllText(secondJournal,
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
            var repositoryRevision = RunCheckedOutput(repository, "git", "rev-parse", "HEAD").Trim();

            var manifest = Path.Combine(root, "manifest.csv");
            File.WriteAllLines(manifest,
            [
                "path,sha256,lastWriteUtc",
                ManifestRow(firstLog, "2026-07-06T05:00:00+00:00"),
                ManifestRow(secondLog, "2026-07-06T06:00:00+00:00"),
                ManifestRow(thirdLog, "2026-07-06T07:00:00+00:00")
            ]);
            var manifestDigest = NormalizedDigest(
                $"{firstLog}|{FileHash(firstLog)}|2026-07-06T05:00:00+00:00",
                $"{secondLog}|{FileHash(secondLog)}|2026-07-06T06:00:00+00:00",
                $"{thirdLog}|{FileHash(thirdLog)}|2026-07-06T07:00:00+00:00");
            var journalManifest = Path.Combine(root, "journal-manifest.csv");
            File.WriteAllLines(journalManifest,
            [
                "path,sha256",
                $"{Path.GetFileName(firstJournal)},{FileHash(firstJournal)}",
                $"{Path.GetFileName(secondJournal)},{FileHash(secondJournal)}"
            ]);
            var journalManifestDigest = NormalizedDigest(
                $"{Path.GetFileName(firstJournal)}|{FileHash(firstJournal)}",
                $"{Path.GetFileName(secondJournal)}|{FileHash(secondJournal)}");
            var metadata = Path.Combine(root, "metadata.csv");
            File.WriteAllText(metadata,
                "goal,selectedPipeline,complexity,risk,criterionCount,criterionOwners,changedPathScope\n" +
                "aaaa1111000000000000000000000000,Developer>Reviewer,Complex,routed,2,\"Planner;Reviewer\",src+tests\n");

            var script = Path.Combine(InfrastructureTestSupport.FindRepositoryRoot(), "scripts", "Analyze-ExcessWorkerRounds.ps1");
            return new AnalysisFixture(
                root, script, manifest, manifestDigest, journals, journalManifest, journalManifestDigest,
                dogfood, FileHash(dogfood), repository, repositoryRevision, metadata, FileHash(metadata), firstLog, firstJournal);
        }

        public ProcessResult Run(
            string outputName,
            string? dogfoodDigest = null,
            string? dogfoodWalDigest = null,
            string? dogfoodPath = null)
        {
            var output = Path.Combine(Root, outputName);
            var selectedDogfoodPath = dogfoodPath ?? _dogfood;
            var arguments = new List<string>
            {
                "-NoProfile", "-File", _script,
                "-OperatorLogManifest", _manifest,
                "-OperatorLogManifestDigestSha256", _manifestDigest,
                "-JournalRoot", _journals,
                "-JournalManifest", _journalManifest,
                "-JournalManifestDigestSha256", _journalManifestDigest,
                "-DogfoodDbPath", selectedDogfoodPath,
                "-DogfoodDbSha256", dogfoodDigest ?? _dogfoodDigest
            };
            if (dogfoodWalDigest is not null)
                arguments.AddRange(["-DogfoodWalSha256", dogfoodWalDigest]);
            arguments.AddRange([
                "-RepositoryRoot", _repository,
                "-RepositoryRevision", _repositoryRevision,
                "-StartWeek", "2026-W27",
                "-EndWeek", "2026-W28",
                "-TimeZoneId", "America/Chicago",
                "-CutoffUtc", "2026-08-07T22:43:03.4206169Z",
                "-TaskMetadataSnapshot", _metadata,
                "-TaskMetadataSnapshotSha256", _metadataDigest,
                "-OutputDirectory", output]);
            return RunProcess(Root, "pwsh", [.. arguments]);
        }

        public void RefreshFirstJournalDigest()
        {
            File.WriteAllLines(_journalManifest,
            [
                "path,sha256",
                $"{Path.GetFileName(FirstJournalPath)},{FileHash(FirstJournalPath)}",
                $"cccc3333000000000000000000000000.jsonl,{FileHash(Path.Combine(_journals, "cccc3333000000000000000000000000.jsonl"))}"
            ]);
            _journalManifestDigest = NormalizedDigest(
                $"{Path.GetFileName(FirstJournalPath)}|{FileHash(FirstJournalPath)}",
                $"cccc3333000000000000000000000000.jsonl|{FileHash(Path.Combine(_journals, "cccc3333000000000000000000000000.jsonl"))}");
        }

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        private static string ManifestRow(string path, string timestamp) =>
            $"\"{path.Replace("\"", "\"\"")}\",{FileHash(path)},{timestamp}";

        public static string FileHash(string path) =>
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

        private static string NormalizedDigest(params string[] rows) =>
            Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(string.Join('\n', rows)))).ToLowerInvariant();
    }


    private static async Task ExecuteAsync(SqliteConnection connection, string commandText)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task CreateFrozenWalInputAsync(
        string sourceDatabasePath,
        string destinationDatabasePath,
        string goalId)
    {
        await using (var connection = new SqliteConnection($"Data Source={sourceDatabasePath};Pooling=False"))
        {
            await connection.OpenAsync();
            await ExecuteAsync(connection, "PRAGMA wal_autocheckpoint=0;");
            await using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO dogfood_log (goal_id, recorded_at, header, summary, operator_gate, model_fit, rendered_markdown) VALUES ($goalId, '2026-07-06T00:00:00Z', 'fixture', 'fixture', 'pass', 'fixture', 'fixture');";
            command.Parameters.AddWithValue("$goalId", goalId);
            await command.ExecuteNonQueryAsync();

            await CopyOpenSqliteFileAsync(sourceDatabasePath, destinationDatabasePath);
            await CopyOpenSqliteFileAsync(sourceDatabasePath + "-wal", destinationDatabasePath + "-wal");
        }
    }

    private static async Task CopyOpenSqliteFileAsync(string sourcePath, string destinationPath)
    {
        await using var source = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        await using var destination = new FileStream(destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await source.CopyToAsync(destination);
    }

    private static void RunChecked(string workingDirectory, string fileName, params string[] arguments)
    {
        var result = RunProcess(workingDirectory, fileName, arguments);
        Xunit.Assert.True(result.ExitCode == 0, result.Stdout + result.Stderr);
    }

    private static string RunCheckedOutput(string workingDirectory, string fileName, params string[] arguments)
    {
        var result = RunProcess(workingDirectory, fileName, arguments);
        Xunit.Assert.True(result.ExitCode == 0, result.Stdout + result.Stderr);
        return result.Stdout;
    }

    private static ProcessResult RunProcess(string workingDirectory, string fileName, params string[] arguments) =>
        RunProcessCore(workingDirectory, fileName, arguments, ambientOverrides: null);

    private static ProcessResult RunProcessWithAmbientOverrides(
        string workingDirectory,
        string fileName,
        IReadOnlyDictionary<string, string> ambientOverrides,
        params string[] arguments) =>
        RunProcessCore(workingDirectory, fileName, arguments, ambientOverrides);

    private static ProcessResult RunProcessCore(
        string workingDirectory,
        string fileName,
        string[] arguments,
        IReadOnlyDictionary<string, string>? ambientOverrides)
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
        if (ambientOverrides is not null)
        {
            foreach (var (name, value) in ambientOverrides) startInfo.Environment[name] = value;
        }
        UseHermeticEnvironment(startInfo, workingDirectory);
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException($"Could not start {fileName}.");
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(TimeSpan.FromSeconds(60)))
        {
            try { process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            process.WaitForExit(5_000);
            throw new TimeoutException($"{fileName} did not exit within 60 seconds.");
        }
        var stdout = stdoutTask.GetAwaiter().GetResult();
        var stderr = stderrTask.GetAwaiter().GetResult();
        return new ProcessResult(process.ExitCode, stdout, stderr);
    }

    private static void UseHermeticEnvironment(ProcessStartInfo startInfo, string workingDirectory)
    {
        string[] requiredNames =
        [
            "COMSPEC", "LOCALAPPDATA", "PATH", "PATHEXT", "PROGRAMDATA", "PROGRAMFILES",
            "PROGRAMFILES(X86)", "SYSTEMDRIVE", "SYSTEMROOT", "TEMP", "TMP", "WINDIR"
        ];
        var required = requiredNames
            .Select(name => (Name: name, Value: startInfo.Environment[name]))
            .Where(item => !string.IsNullOrWhiteSpace(item.Value))
            .ToArray();

        startInfo.Environment.Clear();
        foreach (var (name, value) in required) startInfo.Environment[name] = value!;
        startInfo.Environment["HOME"] = workingDirectory;
        startInfo.Environment["USERPROFILE"] = workingDirectory;
        startInfo.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        startInfo.Environment["GIT_CONFIG_GLOBAL"] = "NUL";
        startInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
    }

    private sealed record ProcessResult(int ExitCode, string Stdout, string Stderr);
}
