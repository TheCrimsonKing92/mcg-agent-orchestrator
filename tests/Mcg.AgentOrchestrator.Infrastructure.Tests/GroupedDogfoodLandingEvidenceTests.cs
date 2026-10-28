using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

// Parallel-safe: every case owns its Git repository, SQLite stores and retained artifacts.
public sealed class GroupedDogfoodLandingEvidenceTests : AcceptanceCohortWorkflowTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void LandedGroup_ProjectsMatchingAcceptance(bool train, bool olderSoloFailure)
    {
        using var fixture = CreateFixture(train, olderSoloFailure);
        Assert.NotEmpty(fixture.Goal.OutstandingCriterionEvidenceObligations);
        var before = fixture.Snapshot();
        var evidence = fixture.Resolve();
        Assert.Equal(DogfoodAcceptanceDisposition.Passed, evidence.AcceptanceDisposition);
        Assert.Equal(new DogfoodLandingProvenance(train ? "merge-train" : "cohort", fixture.Goal.Id.Value,
            fixture.Group, "receipt-matching", fixture.Candidate, fixture.Tree, fixture.Main, fixture.Promoted,
            "finalized-member-landing"), evidence.Provenance);
        var record = fixture.Record();
        Assert.Contains("Acceptance passed.", record.Summary);
        Assert.Contains($"candidate={fixture.Candidate}", record.Summary);
        Assert.Contains($"tree={fixture.Tree}", record.Summary);
        Assert.Contains($"observed-main={fixture.Main}", record.Summary);
        Assert.Contains($"promoted-main={fixture.Promoted}", record.Summary);
        Assert.Equal(before, fixture.Snapshot());
        // Acceptance's managed pre-repair control restores the journal-only recorder wiring;
        // this same fixture and assertion must then fail, without a production control switch.
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PromotionBeforeFinalization_ProjectsOnlyMatchingPassedReceipt(bool train)
    {
        using var fixture = CreateFixture(train, true, finalized: false);
        fixture.RecordPromotion();
        var before = fixture.Snapshot();
        var evidence = fixture.Resolve();
        Assert.Equal(DogfoodAcceptanceDisposition.Passed, evidence.AcceptanceDisposition);
        Assert.Equal(train ? GoalPromotionFact.MergeTrainProvenance : GoalPromotionFact.CohortProvenance,
            evidence.Provenance!.LandingAuthority);
        fixture.Record();
        Assert.Equal(before, fixture.Snapshot());
    }

    public static IEnumerable<object[]> Rejections()
    {
        foreach (var train in new[] { false, true })
        foreach (var fault in new[] { "candidate", "member", "tree", "main", "promoted-main", "receipt",
            "group", "failed", "invalidated", "unlanded", "missing-receipt", "corrupt-artifact",
            "corrupt-store", "promotion-candidate", "promotion-main", "promotion-target", "promotion-only",
            "intent", "conflict", "missing-store", "receipt-only", "missing-artifact" }) yield return [train, fault];
    }

    [Theory]
    [MemberData(nameof(Rejections))]
    public void GroupAuthority_MismatchesNeverProjectSuccess(bool train, string fault)
    {
        using var fixture = CreateFixture(train, false);
        // An unrelated solo pass must never fill a hole in a referenced grouped landing.
        GoalOperationJournal.AcceptancePassed(fixture.Root, fixture.Goal, "solo", fixture.Main, fixture.Main);
        fixture.RecordPromotion();
        fixture.Corrupt(fault);
        var before = fixture.Snapshot();
        var evidence = fixture.Resolve();
        Assert.NotEqual(DogfoodAcceptanceDisposition.Passed, evidence.AcceptanceDisposition);
        Assert.False(string.IsNullOrWhiteSpace(evidence.Reason), $"Missing concrete reason for {fault}");
        Assert.Null(evidence.Provenance);
        Assert.DoesNotContain("Acceptance passed.", fixture.Record().Summary);
        Assert.Equal(before, fixture.Snapshot());
    }

    [Fact]
    public void SoloAndManual_PreserveEvidenceAndObligations()
    {
        var root = CreateAcceptanceCohortRepository();
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var goal = CreateCompletedGoal(kernel, "Preserve operator evidence", root);
            kernel.RecordGoalRefinement(goal.Id, new RefinedSpec("Human inspection remains required",
                ["Operator inspects the external result"], VerificationClass.RealWorldDependent, [], [])
                { OperatorOwnedAcceptanceCriteria = ["Operator inspects the external result"] });
            Assert.NotEmpty(goal.OutstandingCriterionEvidenceObligations);
            var obligations = JsonSerializer.Serialize(goal.CriterionEvidenceObligations);
            var sha = RunGitOutput(root, "rev-parse", "HEAD").Trim();
            GoalOperationJournal.AcceptanceFailed(root, goal, "solo", sha, sha);
            GoalOperationJournal.RecordLandingIntent(root, goal, GoalWorktrees.BranchName(goal.Id), "main",
                sha, "goal-mark-landed");
            var journal = GoalOperationJournal.Read(root, goal.Id);
            var before = JsonSerializer.Serialize(journal);
            var workspace = OrchestratorWorkspace.ForDirectory(root);
            var record = GoalLandingPostActions.RecordDogfoodEntry(goal, root, workspace.DogfoodLogStorePath);
            Assert.Contains("Manual landing recorded; acceptance not recorded as passed. Acceptance failed.", record.Summary);
            Assert.Equal(before, JsonSerializer.Serialize(GoalOperationJournal.Read(root, goal.Id)));
            Assert.Equal(obligations, JsonSerializer.Serialize(goal.CriterionEvidenceObligations));
            Assert.NotEmpty(goal.OutstandingCriterionEvidenceObligations);
            Assert.False(File.Exists(Path.Combine(workspace.OrchestratorDirectory, "cohort-acceptance.db")));
            Assert.False(File.Exists(Path.Combine(workspace.OrchestratorDirectory, "merge-train-acceptance.db")));
            File.WriteAllText(Path.Combine(workspace.OrchestratorDirectory, "cohort-acceptance.db"), "unrelated unavailable source");
            Assert.Equal(record.Summary, GoalLandingPostActions.RecordDogfoodEntry(goal, root, workspace.DogfoodLogStorePath).Summary);
            GoalOperationJournal.AcceptancePassed(root, goal, "solo-retry", sha, sha);
            record = GoalLandingPostActions.RecordDogfoodEntry(goal, root, workspace.DogfoodLogStorePath);
            Assert.Contains("Manual landing recorded; acceptance passed earlier.", record.Summary);
            Assert.Equal(obligations, JsonSerializer.Serialize(goal.CriterionEvidenceObligations));
        }
        finally { DeleteDirectory(root); }
    }

    public static IEnumerable<object[]> SupersedingLandings()
    {
        foreach (var train in new[] { false, true })
        foreach (var manual in new[] { false, true })
        foreach (var outcome in new[] { "passed", "failed", "unknown" }) yield return [train, manual, outcome];
    }

    [Theory]
    [MemberData(nameof(SupersedingLandings))]
    public void PreparedGroup_PreservesSeparateSoloAndManualLanding(bool train, bool manual, string outcome)
    {
        using var fixture = CreateFixture(train, false, finalized: false);
        fixture.AssertPreparedLanding();
        if (outcome == "passed")
            GoalOperationJournal.AcceptancePassed(fixture.Root, fixture.Goal, "solo", fixture.Candidate, fixture.Main);
        else if (outcome == "failed")
            GoalOperationJournal.AcceptanceFailed(fixture.Root, fixture.Goal, "solo", fixture.Candidate, fixture.Main);
        GoalOperationJournal.RecordLandingIntent(fixture.Root, fixture.Goal, GoalWorktrees.BranchName(fixture.Goal.Id),
            "main", fixture.Candidate, manual ? "goal-mark-landed" : "LandingExecutor", boundMainRevision: fixture.Main);
        if (!manual)
            InfrastructureTestSupport.CreateMigratedStateRepository(fixture.Workspace.SqliteStatePath).RecordPromotionFacts([
                new(fixture.Goal.Id.Value, fixture.Candidate, fixture.Main, fixture.Candidate, DateTimeOffset.UtcNow,
                    GoalPromotionFact.SoloProvenance)]);

        var solo = GoalLandingPostActions.ResolveDogfoodLandingEvidence(GoalOperationJournal.Read(fixture.Root, fixture.Goal.Id));
        Assert.Equal(outcome switch
        {
            "passed" => DogfoodAcceptanceDisposition.Passed,
            "failed" => DogfoodAcceptanceDisposition.Failed,
            _ => DogfoodAcceptanceDisposition.Unknown
        }, solo.AcceptanceDisposition);
        Assert.Equal(manual, solo.WasManuallyLanded);
        var before = fixture.Snapshot();
        Assert.Equal(solo, fixture.Resolve());
        var expected = DogfoodLogRenderer.Render(fixture.Goal, solo);
        var recorded = fixture.Record();
        Assert.Equal(expected.Summary, recorded.Summary);
        Assert.Equal(expected.ModelFit, recorded.ModelFit);
        Assert.Contains("OpenAI/original-worker-model", recorded.ModelFit);
        Assert.NotEmpty(fixture.Goal.OutstandingCriterionEvidenceObligations);
        Assert.Equal(before, fixture.Snapshot());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BoundaryRecording_RepeatedCallsPreserveOneProjection(bool train)
    {
        using var fixture = CreateFixture(train, true);
        var old = GoalLandingPostActions.ResolveDogfoodLandingEvidence(GoalOperationJournal.Read(fixture.Root, fixture.Goal.Id));
        var rendered = DogfoodLogRenderer.Render(fixture.Goal, old);
        var store = new DogfoodLogStore(fixture.Workspace.DogfoodLogStorePath);
        var stale = await store.UpsertAsync(new(fixture.Goal.Id.Value, rendered.Header, rendered.Summary,
            rendered.OperatorGate, rendered.ModelFit, rendered.Render()));
        Assert.Contains("Acceptance failed.", stale.Summary);
        var before = fixture.Snapshot();
        var first = fixture.Record();
        var second = fixture.Record();
        var context = CreateAcceptanceContext(fixture.Kernel, fixture.Root, fixture.Goal);
        CaptureConsole(() => CliCommandHandlers.Execute(["dogfood-log", "add", fixture.Goal.Id.Value], context));
        CaptureConsole(() => CliCommandHandlers.Execute(["dogfood-log", "record", fixture.Goal.Id.Value], context));
        var only = Assert.Single(await store.ListRecentAsync());
        Assert.Equal(stale.Sequence, only.Sequence);
        Assert.Equal(first.Summary, second.Summary);
        Assert.Equal(first.RenderedMarkdown, only.RenderedMarkdown);
        Assert.Equal(stale.ModelFit, only.ModelFit);
        Assert.Contains("OpenAI/original-worker-model", only.RenderedMarkdown);
        Assert.Equal(before, fixture.Snapshot());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GoalLookup_DoesNotReadUnrelatedAuthority(bool train)
    {
        using var fixture = CreateFixture(train, true);
        fixture.AddUnrelatedCorruption();
        var before = fixture.Snapshot();
        var result = fixture.Resolve();
        Assert.Equal(DogfoodAcceptanceDisposition.Passed, result.AcceptanceDisposition);
        Assert.Equal(fixture.Group, result.Provenance!.GroupId);
        Assert.Contains("Acceptance passed.", fixture.Record().Summary);
        Assert.Equal(before, fixture.Snapshot());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ConflictingTerminalReferences_StayInconclusive(bool train)
    {
        using var fixture = CreateFixture(train, false);
        fixture.AddConflictingLanding();
        var result = fixture.Resolve();
        Assert.Equal(DogfoodAcceptanceDisposition.Inconclusive, result.AcceptanceDisposition);
        Assert.Contains("conflicts", result.Reason);
    }

    [Fact]
    public void DeclaredCohortReceipt_ValidatesRetainedChecksBeforeProjection()
    {
        using var fixture = new CohortNormalizedFixture();
        var receipt = fixture.Save(fixture.Verification(fixture.Normalized()));
        Assert.True(receipt.HasAuthoritativeLandingEvidence);
        var store = new CohortAcceptanceStore(fixture.StorePath);
        var promoted = new string('e', 40);
        store.PrepareLanding(receipt, promoted);
        store.FinalizeLanding(receipt.Identity.Value, receipt.ReceiptId);
        store.CompleteLandingEffects(receipt.Identity.Value, receipt.ReceiptId);
        var goal = receipt.Identity.Members[0].GoalId;
        var journal = GoalOperationJournal.Read(fixture.Root, goal);
        var calls = new List<string[]>();
        DogfoodLandingEvidence Resolve() => DogfoodLandingEvidenceResolver.Resolve(goal, fixture.Root,
            fixture.OrchestratorRoot, journal, DogfoodLandingEvidence.Unknown, args =>
            {
                calls.Add(args);
                return (0, args[^1].EndsWith("^{tree}", StringComparison.Ordinal)
                    ? receipt.Identity.CombinedTreeRevision : args[^1].StartsWith(promoted, StringComparison.Ordinal)
                        ? promoted : receipt.Identity.Members[0].CandidateRevision);
            });
        Assert.Equal(DogfoodAcceptanceDisposition.Passed, Resolve().AcceptanceDisposition);
        Assert.Contains(calls, args => args[0] == "merge-base");
        File.WriteAllText(receipt.GateChecks.Single().Artifacts.Single().Path, "invalid declared evidence");
        var rejected = Resolve();
        Assert.Equal(DogfoodAcceptanceDisposition.Inconclusive, rejected.AcceptanceDisposition);
        Assert.Contains("content-bound", rejected.Reason);
    }

    private static Fixture CreateFixture(bool train, bool olderFailure, bool finalized = true)
    {
        var root = CreateAcceptanceCohortRepository();
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var task = new TaskSpec(TaskId.New(), "Implement grouped evidence", AgentRole.Developer);
            var goal = kernel.CreateGoal("Grouped landing projection", [task]);
            kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
            kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("original-launcher", "worker", root,
                DateTimeOffset.UtcNow, ProviderName: "OpenAI", ModelName: "original-worker-model"));
            kernel.RecordTaskVerification(goal.Id, task.Id, new TaskVerificationRecord("worker", root, 0,
                "WORKER_RESULT:\nmodel_fit: OpenAI/original-worker-model - adequate - original implementation\nEND_WORKER_RESULT",
                "", DateTimeOffset.UtcNow));
            kernel.RecordGoalRefinement(goal.Id, new RefinedSpec("Grouped projection", ["Human observation"],
                VerificationClass.RealWorldDependent, [], []) { OperatorOwnedAcceptanceCriteria = ["Human observation"] });
            var main = RunGitOutput(root, "rev-parse", "main").Trim();
            var first = CreateCandidate(root, goal.Id.Value, "src/First.cs", "first");
            var second = CreateCandidate(root, GoalId.New().Value, "src/Second.cs", "second");
            RunGit(root, "merge", "--no-ff", "-m", "Combine first member", first.Revision);
            RunGit(root, "merge", "--no-ff", "-m", "Combine second member", second.Revision);
            var promoted = RunGitOutput(root, "rev-parse", "HEAD").Trim();
            var tree = RunGitOutput(root, "rev-parse", "HEAD^{tree}").Trim();
            var workspace = OrchestratorWorkspace.ForDirectory(root);
            Directory.CreateDirectory(workspace.OrchestratorDirectory);
            var trx = WritePassingTrx(root, "passing.trx");
            string group, artifact;
            if (train)
            {
                var identity = MergeTrainIdentity.Create([
                    TrainBind(goal.Id, first.Revision, "src/First.cs", "first").WithRebasedHead(first.Revision),
                    TrainBind(second.GoalId, second.Revision, "src/Second.cs", "second").WithRebasedHead(promoted)], main, tree, "manifest");
                var store = new MergeTrainAcceptanceStore(Path.Combine(workspace.OrchestratorDirectory, "merge-train-acceptance.db"));
                var receipt = store.SaveGateReceipt(new("receipt-matching", identity, MergeTrainGateOutcome.Passed,
                    DateTimeOffset.UtcNow, 1, [], 0, [trx], true));
                Assert.True(receipt.HasAuthoritativeLandingEvidence);
                store.PrepareLanding(receipt, promoted, main);
                if (finalized) { store.FinalizeLanding(identity.Value, receipt.ReceiptId); store.CompleteLandingEffects(identity.Value, receipt.ReceiptId); }
                group = identity.Value;
                artifact = receipt.GateTestResultPaths.Single();
            }
            else
            {
                var identity = AcceptanceCohortIdentity.Create([
                    Bind(goal.Id, first.Revision, "src/First.cs", "first"),
                    Bind(second.GoalId, second.Revision, "src/Second.cs", "second")], main, tree, "manifest");
                var store = new CohortAcceptanceStore(Path.Combine(workspace.OrchestratorDirectory, "cohort-acceptance.db"));
                var receipt = store.SaveGateReceipt(new("receipt-matching", identity, AcceptanceCohortGateOutcome.Passed,
                    DateTimeOffset.UtcNow, 1, [], 0, [trx], ValidForLanding: true));
                Assert.True(receipt.HasAuthoritativeLandingEvidence);
                store.PrepareLanding(receipt, promoted, main);
                if (finalized) { store.FinalizeLanding(identity.Value, receipt.ReceiptId); store.CompleteLandingEffects(identity.Value, receipt.ReceiptId); }
                group = identity.Value;
                artifact = receipt.GateTestResultPaths.Single();
            }
            if (olderFailure) GoalOperationJournal.AcceptanceFailed(root, goal, "old-solo", first.Revision, main);
            GoalOperationJournal.RecordLandingIntent(root, goal, GoalWorktrees.BranchName(goal.Id),
                $"{(train ? "train" : "cohort")}/{group}", promoted, train ? "LandingExecutor.ExecuteMergeTrain" : "LandingExecutor.ExecuteCohort",
                boundMainRevision: main, previousIntegrationRevision: main);
            return new(root, kernel, goal, workspace, train, group, first.Revision, tree, main, promoted, artifact);
        }
        catch { DeleteDirectory(root); throw; }
    }

    private sealed record Fixture(string Root, AgentOrchestratorKernel Kernel, Goal Goal, OrchestratorWorkspace Workspace,
        bool Train, string Group, string Candidate, string Tree, string Main, string Promoted, string Artifact) : IDisposable
    {
        private string Database => Path.Combine(Workspace.OrchestratorDirectory,
            Train ? "merge-train-acceptance.db" : "cohort-acceptance.db");
        private string Receipts => Train ? "merge_train_receipts" : "cohort_receipts";
        private string Landings => Train ? "merge_train_landings" : "cohort_landing_intents";
        private string Members => Train ? "merge_train_receipt_members" : "cohort_members";
        private string Key => Train ? "train_id" : "cohort_id";
        private string CommitColumn => Train ? "commit_revision" : "combined_commit_revision";

        public DogfoodLogRecord Record() => GoalLandingPostActions.RecordDogfoodEntry(Goal, Root, Workspace.DogfoodLogStorePath);
        public DogfoodLandingEvidence Resolve()
        {
            var journal = GoalOperationJournal.Read(Root, Goal.Id);
            return DogfoodLandingEvidenceResolver.Resolve(Goal.Id, Root, Workspace.OrchestratorDirectory, journal,
                GoalLandingPostActions.ResolveDogfoodLandingEvidence(journal));
        }
        public void RecordPromotion() => InfrastructureTestSupport.CreateMigratedStateRepository(Workspace.SqliteStatePath).RecordPromotionFacts([
            new(Goal.Id.Value, Candidate, Main, Promoted, DateTimeOffset.UtcNow,
                Train ? GoalPromotionFact.MergeTrainProvenance : GoalPromotionFact.CohortProvenance)]);

        public void AssertPreparedLanding()
        {
            using var connection = new SqliteConnection($"Data Source={Database};Mode=ReadOnly;Pooling=False");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT state, {CommitColumn} FROM {Landings} WHERE {Key}=$group";
            command.Parameters.AddWithValue("$group", Group);
            using var reader = command.ExecuteReader();
            Assert.True(reader.Read());
            Assert.Equal("prepared", reader.GetString(0));
            Assert.Equal(Promoted, reader.GetString(1));
            Assert.False(reader.Read());
        }

        public void Corrupt(string fault)
        {
            var zero = new string('0', 40);
            switch (fault)
            {
                case "candidate": ChangeReceipt("CandidateRevision", "candidate_revision", zero); break;
                case "member":
                    if (Train) ChangeReceipt("GoalId", "goal_id", "unrelated");
                    else Execute(Database, $"UPDATE {Members} SET goal_id='unrelated' WHERE goal_id='{Goal.Id.Value}'");
                    break;
                case "tree": ChangeReceipt("TrainTreeRevision", "combined_tree_revision", zero); break;
                case "main": ChangeReceipt("ObservedMainRevision", "main_revision", zero); break;
                case "promoted-main": Execute(Database, $"UPDATE {Landings} SET {CommitColumn}='{Main}'"); break;
                case "receipt": Execute(Database, $"UPDATE {Landings} SET receipt_id='other-receipt'"); break;
                case "group": Execute(Database, $"UPDATE {Members} SET {Key}='wrong-group'"); break;
                case "failed": ChangeReceipt("Outcome", "outcome", Train ? "1" : "Failed", numeric: Train); break;
                case "invalidated": ChangeReceipt("Outcome", "outcome", Train ? "3" : "Invalidated", numeric: Train); break;
                case "unlanded":
                    Execute(Database, $"UPDATE {Landings} SET state='prepared'");
                    Execute(Workspace.SqliteStatePath, "DELETE FROM goal_promotion_facts");
                    break;
                case "missing-receipt":
                case "promotion-only": Execute(Database, $"DELETE FROM {Receipts}"); break;
                case "corrupt-artifact": File.WriteAllText(Artifact, "corrupt retained evidence"); break;
                case "missing-artifact": File.Delete(Artifact); break;
                case "missing-store": File.Delete(Database); break;
                case "receipt-only":
                    Execute(Database, $"DELETE FROM {Landings}");
                    Execute(Workspace.SqliteStatePath, "DELETE FROM goal_promotion_facts");
                    break;
                case "corrupt-store": File.WriteAllText(Database, "invalid SQLite"); break;
                case "promotion-candidate": Execute(Workspace.SqliteStatePath, $"UPDATE goal_promotion_facts SET candidate_revision='{Main}'"); break;
                case "promotion-main": Execute(Workspace.SqliteStatePath, $"UPDATE goal_promotion_facts SET prior_main_revision='{Candidate}'"); break;
                case "promotion-target": Execute(Workspace.SqliteStatePath, $"UPDATE goal_promotion_facts SET promoted_main_revision='{Candidate}'"); break;
                case "intent": GoalOperationJournal.RecordLandingIntent(Root, Goal, "goal", "group", Main, "native-group", boundMainRevision: Main); break;
                case "conflict":
                    Execute(Database, $"UPDATE {Landings} SET prior_integration_revision='{Promoted}'");
                    break;
                default: throw new ArgumentOutOfRangeException(nameof(fault));
            }
        }

        private void ChangeReceipt(string jsonProperty, string column, string value, bool numeric = false)
        {
            if (!Train)
            {
                var table = column is "candidate_revision" or "goal_id" ? Members : Receipts;
                Execute(Database, $"UPDATE {table} SET {column}='{(value == "false" ? "0" : value)}'");
                return;
            }
            using var connection = new SqliteConnection($"Data Source={Database};Pooling=False");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT payload_json FROM merge_train_receipts";
            var node = System.Text.Json.Nodes.JsonNode.Parse((string)command.ExecuteScalar()!)!;
            var property = char.ToLowerInvariant(jsonProperty[0]) + jsonProperty[1..];
            if (jsonProperty is "CandidateRevision" or "GoalId")
            {
                if (jsonProperty == "GoalId") node["members"]![0]![property]!["value"] = value;
                else node["members"]![0]![property] = value;
            }
            else node[property] = numeric ? System.Text.Json.Nodes.JsonNode.Parse(value) : JsonSerializer.SerializeToNode(value);
            command.CommandText = "UPDATE merge_train_receipts SET payload_json=$payload";
            command.Parameters.AddWithValue("$payload", node.ToJsonString());
            command.ExecuteNonQuery();
        }

        public void AddUnrelatedCorruption()
        {
            Execute(Workspace.SqliteStatePath, "CREATE TABLE IF NOT EXISTS goal_promotion_facts(goal_id TEXT PRIMARY KEY, candidate_revision TEXT, prior_main_revision TEXT, promoted_main_revision TEXT, promoted_at TEXT, provenance TEXT)");
            Execute(Workspace.SqliteStatePath, "INSERT INTO goal_promotion_facts VALUES('unrelated','bad','bad','bad','bad','bad')");
            if (Train)
            {
                Execute(Database, "INSERT INTO merge_train_receipts VALUES('unrelated','broken','not-json')");
                Execute(Database, "INSERT INTO merge_train_receipt_members VALUES('unrelated-goal','unrelated')");
            }
            else
            {
                // A malformed unrelated membership must never trigger canonical reconstruction.
                Execute(Database, $"INSERT INTO cohort_members SELECT 'unrelated',member_ordinal,'unrelated-goal',branch_revision,candidate_revision,landing_paths_json,resource_keys_json,risk_tier,promotion_disposition,merge_status,merge_reason,landed,brief_version FROM cohort_members WHERE goal_id='{Goal.Id.Value}'");
            }
        }

        public void AddConflictingLanding()
        {
            if (Train)
            {
                var store = new MergeTrainAcceptanceStore(Database);
                var original = store.TryReadReceipt(Group)!;
                var identity = MergeTrainIdentity.Create(original.Identity.Members, Main, Tree, "other-manifest");
                var receipt = store.SaveGateReceipt(original with { ReceiptId = "other-receipt", Identity = identity });
                Assert.True(receipt.HasAuthoritativeLandingEvidence);
                store.PrepareLanding(receipt, Promoted, Main);
                store.FinalizeLanding(identity.Value, receipt.ReceiptId);
                store.CompleteLandingEffects(identity.Value, receipt.ReceiptId);
            }
            else
            {
                var store = new CohortAcceptanceStore(Database);
                var original = store.TryReadReceipt(Group)!;
                var identity = AcceptanceCohortIdentity.Create(original.Identity.Members, Main, Tree, "other-manifest");
                var receipt = store.SaveGateReceipt(original with { ReceiptId = "other-receipt", Identity = identity });
                Assert.True(receipt.HasAuthoritativeLandingEvidence);
                store.PrepareLanding(receipt, Promoted, Main);
                store.FinalizeLanding(identity.Value, receipt.ReceiptId);
                store.CompleteLandingEffects(identity.Value, receipt.ReceiptId);
            }
        }

        public string Snapshot() => JsonSerializer.Serialize(new
        {
            Journal = GoalOperationJournal.Read(Root, Goal.Id),
            Obligations = Goal.CriterionEvidenceObligations,
            Stores = Directory.GetFiles(Workspace.OrchestratorDirectory, "*.db").Where(p => !p.EndsWith("dogfood-log.db", StringComparison.Ordinal))
                .Order(StringComparer.Ordinal).Select(p => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(p)))).ToArray()
        });
        public void Dispose() => DeleteDirectory(Root);
        private static void Execute(string path, string sql)
        {
            using var connection = new SqliteConnection($"Data Source={path};Pooling=False;Foreign Keys=False");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }
    }
}
