using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: repositories, build storage, command execution and attempt stores are fixture-local.
public sealed class PartialNegativeControlResumeTests
{
    [Xunit.Fact]
    public void AbsentHandoff_CapacityDeferral_ReexecutesCandidate()
    {
        using var fixture = new Fixture();
        var first = fixture.Run(true, disableContinuation: true);
        Assert.Equal(ConductorParallelAcceptanceAttemptOutcome.BlockedBuildSlot, first.Outcome);
        Assert.Null(first.PartialSourceRevertCandidate);
        fixture.Run(false, disableContinuation: true);
        Assert.Equal(2, fixture.CandidateCalls);
        Assert.Equal(1, fixture.RevertedCalls);
        fixture.AssertRestored();
    }

    [Xunit.Fact]
    public void CapacityDeferral_Reload_ExecutesCandidateExactlyOnce()
    {
        using var fixture = new Fixture();
        var first = fixture.Run(defer: true);
        Assert.Equal(ConductorParallelAcceptanceAttemptOutcome.BlockedBuildSlot, first.Outcome);
        Assert.Null(first.OriginalFocusedReceipt);
        var original = Assert.IsType<SourceRevertedCandidateObservation>(first.PartialSourceRevertCandidate);
        Assert.Equal(first.AttemptId, original.AttemptId);
        Assert.Equal(first.GoalId, original.GoalId);
        Assert.Equal(first.CandidateKey, original.CandidateKey);
        Assert.NotNull(original.RestorationCompletedAt);
        Assert.Equal(original.RestorationCompletedAt, first.PartialSourceRevertRestorationCompletedAt);
        Assert.True(original.ObservedAt <= original.RestorationCompletedAt);
        Assert.True(original.RestorationCompletedAt <= first.CompletedAt);
        Assert.False(string.IsNullOrWhiteSpace(original.OriginalResultFingerprint));
        Assert.False(string.IsNullOrWhiteSpace(original.EvidenceFingerprint));
        Assert.All(original.OriginalArm.ReceiptArtifacts!, artifact => Assert.True(File.Exists(artifact.Path)));
        Assert.Equal(1, fixture.CandidateCalls);
        Assert.Equal(0, fixture.RevertedCalls);
        Assert.Null(fixture.FreshCoordinator().ReadOriginalFocusedReceipt(first));

        var second = fixture.Run(defer: false);
        Assert.Equal(ConductorParallelAcceptanceAttemptOutcome.Passed, second.Outcome);
        Assert.Equal(1, fixture.CandidateCalls);
        Assert.Equal(1, fixture.RevertedCalls);
        Assert.Equal(first.AttemptId, second.PartialSourceRevertCandidate?.AttemptId);
        Assert.Equal(original.OriginalResultFingerprint, second.PartialSourceRevertCandidate?.OriginalResultFingerprint);
        Assert.Equal(FindingEvidenceNegativeControlOutcome.Demonstrated, fixture.LastEvidence!.NegativeControlOutcome);
        fixture.AssertRestored();
    }

    [Xunit.Fact]
    public void RepeatedDeferral_Reload_PreservesOriginalObservation()
    {
        using var fixture = new Fixture();
        var first = fixture.Run(true);
        var second = fixture.Run(true);
        Assert.Equal(ConductorParallelAcceptanceAttemptOutcome.BlockedBuildSlot, second.Outcome);
        Assert.Equal(SourceRevertedEvidenceContinuation.Fingerprint(first.PartialSourceRevertCandidate),
            SourceRevertedEvidenceContinuation.Fingerprint(second.PartialSourceRevertCandidate));
        Assert.Equal(1, fixture.CandidateCalls);
        Assert.Equal(0, fixture.RevertedCalls);
        var third = fixture.Run(false);
        Assert.Equal(ConductorParallelAcceptanceAttemptOutcome.Passed, third.Outcome);
        Assert.Equal(1, fixture.CandidateCalls);
        Assert.Equal(1, fixture.RevertedCalls);
        fixture.AssertRestored();
    }

    [Xunit.Theory]
    [Xunit.InlineData("missing-trx")]
    [Xunit.InlineData("changed-trx")]
    [Xunit.InlineData("malformed-trx")]
    [Xunit.InlineData("missing-result")]
    [Xunit.InlineData("changed-result")]
    [Xunit.InlineData("whitespace-result")]
    [Xunit.InlineData("missing-candidate-result")]
    [Xunit.InlineData("changed-candidate-result")]
    [Xunit.InlineData("legacy")]
    [Xunit.InlineData("chronology")]
    [Xunit.InlineData("restoration")]
    [Xunit.InlineData("original-hash")]
    [Xunit.InlineData("original-arm-red")]
    [Xunit.InlineData("missing-hashes")]
    [Xunit.InlineData("candidate")]
    [Xunit.InlineData("selection")]
    [Xunit.InlineData("revert-set")]
    [Xunit.InlineData("implicit-revert")]
    [Xunit.InlineData("configuration")]
    [Xunit.InlineData("declared-paths")]
    public void InvalidProof_Reload_ExecutesCandidateAgain(string invalidation)
    {
        using var fixture = new Fixture();
        var first = fixture.Run(true);
        Assert.NotNull(first.PartialSourceRevertCandidate);
        fixture.Invalidate(first, invalidation);
        fixture.Run(false);
        Assert.Equal(2, fixture.CandidateCalls);
        fixture.AssertRestored();
    }

    [Xunit.Theory]
    [Xunit.InlineData("green")]
    [Xunit.InlineData("compile")]
    [Xunit.InlineData("cancel")]
    public void ResumedControl_Unsuccessful_RemainsTruthfulAndRestores(string outcome)
    {
        using var fixture = new Fixture();
        fixture.Run(true);
        var next = fixture.Run(false, outcome);
        Assert.Equal(1, fixture.CandidateCalls);
        if (outcome == "cancel")
            Assert.Equal(ConductorParallelAcceptanceAttemptOutcome.Cancelled, next.Outcome);
        else
        {
            Assert.NotNull(fixture.LastEvidence);
            Assert.Equal(outcome == "green" ? FindingEvidenceNegativeControlOutcome.NotDemonstrated :
                FindingEvidenceNegativeControlOutcome.CompileRed, fixture.LastEvidence.NegativeControlOutcome);
        }
        fixture.AssertRestored();
    }

    [Xunit.Fact]
    public void RepeatedDeferral_MissingLatestRestoration_RejectsReuse()
    {
        using var fixture = new Fixture();
        fixture.Run(true);
        var second = fixture.Run(true);
        Assert.Equal(1, fixture.CandidateCalls);
        fixture.Invalidate(second, "attempt-restoration");
        fixture.Run(false);
        Assert.Equal(2, fixture.CandidateCalls);
        Assert.Equal(1, fixture.RevertedCalls);
        fixture.AssertRestored();
    }

    [Xunit.Fact]
    public void NewerCandidateRed_Reload_VetoesOlderGreen()
    {
        using var fixture = new Fixture();
        fixture.Run(true);
        fixture.Run(false, "candidate-red", disableContinuation: true);
        Assert.False(fixture.LastEvidence!.Passed);
        fixture.Run(false);
        Assert.Equal(3, fixture.CandidateCalls);
        fixture.AssertRestored();
    }

    private sealed class Fixture : IDisposable
    {
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
        private readonly FindingBaselineProbeFixture _repo = FindingBaselineProbeFixture.Create();
        private readonly string _authority = Path.Combine(Path.GetTempPath(), "mcg-partial-revert", Guid.NewGuid().ToString("N"));
        private readonly Goal _goal = new(GoalId.New(), "Partial source revert",
            [new TaskSpec(TaskId.New(), "Evidence", AgentRole.Developer)]);
        private readonly DotnetBuildStorageRoot _storage;
        private string _request = "Core.Tests: AddedProbeTests";
        private IReadOnlyList<string>? _paths = ["src/Feature.cs"];
        private IReadOnlyList<string>? _declared;
        private string? _revertedPath;
        private ConductorParallelAcceptanceAttempt? _last;
        internal int CandidateCalls { get; private set; }
        internal int RevertedCalls { get; private set; }
        internal FocusedEvidenceRunResult? LastEvidence { get; private set; }

        internal Fixture()
        {
            Directory.CreateDirectory(_authority);
            _storage = new DotnetBuildStorageRoot(Path.Combine(_authority, "build"));
            File.AppendAllText(Path.Combine(_repo.Root, ".git", "info", "exclude"), "\n.orchestrator/\n");
            Git("checkout", "main");
            Directory.CreateDirectory(Path.Combine(_repo.Root, "src"));
            File.WriteAllText(Path.Combine(_repo.Root, "src/Feature.cs"), "baseline source");
            Git("add", "src"); Git("commit", "-m", "source baseline");
            Git("checkout", "goal/finding-baseline"); Git("merge", "main", "--no-edit");
            File.WriteAllText(Path.Combine(_repo.Root, "src/Feature.cs"), "candidate source");
            Git("add", "src"); Git("commit", "-m", "source candidate");
        }

        internal ConductorParallelAcceptanceAttemptCoordinator FreshCoordinator() => new(
            Path.Combine(_authority, "attempts"), "main", executionDirectory: _repo.Root, runInline: true,
            acquireStableSlotLease: (_, _) => null, buildStorageRoot: _storage);

        internal ConductorParallelAcceptanceAttempt Run(bool defer, string outcome = "red", bool disableContinuation = false)
        {
            // Every invocation uses fresh durable-state and execution owners. No continuation is
            // retained in the fixture; only execution counters cross this boundary.
            var coordinator = FreshCoordinator();
            if (_last is not null) coordinator.MarkReconciled(_last);
            var candidate = ConductorParallelAcceptanceCandidate.Create(_goal, 0, ["src/Feature.cs"],
                Git("rev-parse", "HEAD").Trim(), Git("rev-parse", "main").Trim());
            var overrides = new GoalAcceptanceVerifierTestOverrides
            {
                BuildStorageRootForTests = _storage, BaselineIntegrityLabelerForTests = new Labeler(),
                ShardPermitRootForTests = Path.Combine(_authority, "permits")
            };
            var verifier = new GoalAcceptanceVerifier(overrides, (args, worktree, _) =>
            {
                var reverted = !string.Equals(worktree, _repo.Root, StringComparison.OrdinalIgnoreCase);
                if (reverted)
                {
                    _revertedPath = worktree;
                    Assert.Equal("baseline source", File.ReadAllText(Path.Combine(worktree, "src/Feature.cs")));
                    if (defer) throw new DotnetBuildSlotsBusyException(new("partial-revert-fixture", []));
                    if (outcome == "cancel") throw new OperationCanceledException("controlled reverted cancellation");
                    if (outcome == "compile" && !args.Contains("--results-directory"))
                        return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(1, "error CS0103: reverted API is absent"));
                }
                if (!args.Contains("--results-directory"))
                    return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "Build succeeded."));
                if (reverted) RevertedCalls++; else CandidateCalls++;
                var red = reverted ? outcome != "green" : outcome == "candidate-red";
                WriteTrx(args, red);
                return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(red ? 1 : 0, red ? "Failed: 1" : "Passed: 1"));
            });
            var runner = new ConductorParallelAcceptanceAttemptCoordinator.SourceRevertFocusedRunner((goal, request, lease, token, continuation) =>
            {
                LastEvidence = AcceptanceExecutionRunner.RunFocusedVerification(verifier, _repo.Root, goal.Id, request,
                    null, lease, false, token, "main", FindingEvidenceNegativeControl.RevertSrc, _paths,
                    declaredPaths: _declared, continuation: disableContinuation ? null : continuation);
                return LastEvidence;
            });
            coordinator.EvaluateFocusedEvidence(candidate, ConductorAutonomyPolicy.Conservative, _request, runner.Run,
                new("partial-fixture", "partial-batch", [], NegativeControl: FindingEvidenceNegativeControl.RevertSrc, RevertPaths: _paths));
            _last = coordinator.ReadFocusedCoverageAttempts(_goal.Id).OrderByDescending(a => a.Ordinal).First();
            return _last;
        }

        internal void Invalidate(ConductorParallelAcceptanceAttempt attempt, string kind)
        {
            var observation = attempt.PartialSourceRevertCandidate!;
            var trx = observation.OriginalArm.TestResultPaths!.Single();
            switch (kind)
            {
                case "missing-trx": File.Delete(trx); break;
                case "changed-trx": File.AppendAllText(trx, "<!-- tampered -->"); break;
                case "malformed-trx": File.WriteAllText(trx, "malformed"); break;
                case "missing-result": File.Delete(attempt.ResultPath); break;
                case "changed-result": File.WriteAllText(attempt.ResultPath, "{}"); break;
                case "whitespace-result": File.AppendAllText(attempt.ResultPath, " "); break;
                case "missing-candidate-result": File.Delete(observation.EvidencePath); break;
                case "changed-candidate-result": File.AppendAllText(observation.EvidencePath, " "); break;
                case "legacy": Rewrite(attempt, node => node.AsObject().Remove("partialSourceRevertCandidate")); break;
                case "chronology": Rewrite(attempt, node => node["partialSourceRevertCandidate"]!["observedAt"] = DateTimeOffset.MaxValue); break;
                case "restoration": Rewrite(attempt, node => node["partialSourceRevertCandidate"]!.AsObject().Remove("restorationCompletedAt")); break;
                case "attempt-restoration": Rewrite(attempt, node => node.AsObject().Remove("partialSourceRevertRestorationCompletedAt")); break;
                case "original-hash": Rewrite(attempt, node => node["partialSourceRevertCandidate"]!["originalResultFingerprint"] = "changed"); break;
                case "original-arm-red": Rewrite(attempt, node => node["partialSourceRevertCandidate"]!["originalArm"]!["passed"] = false); break;
                case "missing-hashes": Rewrite(attempt, node => node["partialSourceRevertCandidate"]!["originalArm"]!.AsObject().Remove("receiptArtifacts")); break;
                case "candidate": File.WriteAllText(Path.Combine(_repo.Root, "src/Extra.cs"), "extra"); Git("add", "src"); Git("commit", "-m", "new candidate"); break;
                case "selection": _request = "Core.Tests: AddedProbeTests; Core.Tests: PreExistingProbeTests"; break;
                case "revert-set": _paths = ["src/Feature.cs", "src/Missing.cs"]; break;
                case "implicit-revert": _paths = null; break;
                case "declared-paths": _declared = ["src/Feature.cs"]; break;
                case "configuration":
                    var manifest = Path.Combine(_repo.Root, "config/acceptance-manifest.json");
                    var node = JsonNode.Parse(File.ReadAllText(manifest))!;
                    node["engine"]!["maxConcurrentShards"] = 2;
                    File.WriteAllText(manifest, node.ToJsonString());
                    break;
                default: throw new ArgumentOutOfRangeException(nameof(kind));
            }
        }

        private static void Rewrite(ConductorParallelAcceptanceAttempt attempt, Action<JsonNode> change)
        {
            var node = JsonNode.Parse(File.ReadAllText(attempt.MetadataPath))!;
            change(node);
            File.WriteAllText(attempt.MetadataPath, node.ToJsonString(Json));
        }

        internal void AssertRestored()
        {
            Assert.NotNull(_revertedPath);
            Assert.False(Directory.Exists(_revertedPath));
            Assert.DoesNotContain(_revertedPath.Replace('\\', '/'), Git("worktree", "list", "--porcelain").Replace('\\', '/'));
            Assert.Equal("candidate source", File.ReadAllText(Path.Combine(_repo.Root, "src/Feature.cs")));
        }

        private string Git(params string[] args)
        {
            var result = GitCli.Run(_repo.Root, args);
            Assert.True(result.Succeeded, result.Error);
            return result.Output;
        }

        public void Dispose()
        {
            _repo.Dispose();
            Directory.Delete(_authority, recursive: true);
        }
    }

    private static void WriteTrx(string[] args, bool red)
    {
        var directory = args[Array.IndexOf(args, "--results-directory") + 1];
        Directory.CreateDirectory(directory);
        new XDocument(new XElement("TestRun",
            new XElement("TestDefinitions", new XElement("UnitTest", new XAttribute("id", "probe"),
                new XElement("TestMethod", new XAttribute("className", "AddedProbeTests"), new XAttribute("name", "AddedBehavior")))),
            new XElement("Results", new XElement("UnitTestResult", new XAttribute("testId", "probe"),
                new XAttribute("testName", "AddedProbeTests.AddedBehavior"), new XAttribute("outcome", red ? "Failed" : "Passed"))),
            new XElement("ResultSummary", new XAttribute("outcome", "Completed"), new XElement("Counters",
                new XAttribute("total", 1), new XAttribute("executed", 1), new XAttribute("passed", red ? 0 : 1),
                new XAttribute("failed", red ? 1 : 0), new XAttribute("notExecuted", 0)))))
            .Save(Path.Combine(directory, args[Array.IndexOf(args, "--report-trx-filename") + 1]));
    }

    private sealed class Labeler : IWorkerIntegrityLabeler
    {
        public IntegrityLabelState Query(string path) => new(true, true, true);
        public bool SetIntegrity(string path, string level, bool recursive) => true;
    }
}
