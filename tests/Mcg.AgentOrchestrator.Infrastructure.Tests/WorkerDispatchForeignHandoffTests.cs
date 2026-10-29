using System.Text;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: each preparation owns its temporary directory and in-memory kernel.
public sealed class WorkerDispatchForeignHandoffTests : WorkerDispatchTestSupport
{
    [Fact]
    public void FirstPlanner_ForeignV1_IgnoresAndPreservesBytes()
    {
        using var fixture = new PreparationFixture(CreateTempDirectory());
        var foreignTaskId = TaskId.New();
        var identity = $"prior/{foreignTaskId.Value}/verification-output";
        var bytes = Encoding.UTF8.GetBytes("foreign evidence sentinel — unchanged\r\n");
        var relativePath = $".orchestrator-context/legacy-handoff/{foreignTaskId.Value}/verification-output.bin";
        var materialization = fixture.WriteMaterialization(relativePath, bytes);
        var handoffBytes = fixture.WritePointer(identity, bytes, relativePath);
        Assert.Single(fixture.Goal.Tasks);
        Assert.Null(fixture.Task.LastVerification);

        var result = fixture.Prepare();

        fixture.AssertDeliveryExcludes(result, identity, "foreign evidence sentinel", foreignTaskId.Value);
        Assert.Equal(handoffBytes, File.ReadAllBytes(fixture.HandoffPath));
        Assert.Equal(bytes, File.ReadAllBytes(materialization));
    }

    [Fact]
    public void FirstPlanner_AbsentHandoff_PreparesWithoutLegacyInput()
    {
        using var fixture = new PreparationFixture(CreateTempDirectory());
        Assert.False(File.Exists(fixture.HandoffPath));

        var result = fixture.Prepare();

        fixture.AssertDeliveryExcludes(result, "legacy-handoff/v0/1");
        Assert.False(File.Exists(fixture.HandoffPath));
    }

    [Fact]
    public void FirstPlanner_UserHandoff_IgnoresAndPreservesBytes()
    {
        using var fixture = new PreparationFixture(CreateTempDirectory());
        var bytes = Encoding.UTF8.GetBytes("# User notes\r\nuser handoff sentinel — preserve me\r\n");
        File.WriteAllBytes(fixture.HandoffPath, bytes);

        var result = fixture.Prepare();

        fixture.AssertDeliveryExcludes(result, "user handoff sentinel", "legacy-handoff/v0/1");
        Assert.Equal(bytes, File.ReadAllBytes(fixture.HandoffPath));
    }

    [Fact]
    public void FirstPlanner_MalformedUserHandoff_IgnoresAndPreservesBytes()
    {
        using var fixture = new PreparationFixture(CreateTempDirectory());
        var bytes = Encoding.UTF8.GetBytes(
            "Compatibility pointer (v1, hash-bound; resolve from authoritative task evidence): {not-json}\r\n");
        File.WriteAllBytes(fixture.HandoffPath, bytes);

        var result = fixture.Prepare();

        fixture.AssertDeliveryExcludes(result, "not-json", "legacy-handoff/v0/1");
        Assert.Equal(bytes, File.ReadAllBytes(fixture.HandoffPath));
    }

    [Fact]
    public void SameGoalPrior_DeliversExactIdentityAndHash()
    {
        using var fixture = new PreparationFixture(CreateTempDirectory(), includePrior: true);
        var priorTask = fixture.Goal.Tasks[0];
        fixture.Kernel.ReportTaskProgress(fixture.Goal.Id, priorTask.Id,
            WorkTaskStatus.Completed, "Earlier task completed.");
        fixture.RecordPriorVerification(exitCode: 0);
        var identity = $"prior/{priorTask.Id.Value}/verification-output";
        var expected = Encoding.UTF8.GetBytes(WorkerVerificationEvidence.ProjectPriorTaskOutputForContext(priorTask));

        var result = fixture.Prepare();

        fixture.AssertDeliveredBytes(result, identity, expected);
        Assert.Contains(identity, File.ReadAllText(fixture.HandoffPath), StringComparison.Ordinal);
        Assert.Equal(expected, File.ReadAllBytes(fixture.AbsolutePath(
            $".orchestrator-context/legacy-handoff/{priorTask.Id.Value}/verification-output.bin")));
    }

    [Fact]
    public void SameGoalPrior_RetainedPointer_DeliversExactIdentityAndHash()
    {
        using var fixture = new PreparationFixture(CreateTempDirectory(), includePrior: true);
        fixture.RecordPriorVerification();
        var priorTask = fixture.Goal.Tasks[0];
        Assert.NotEqual(WorkTaskStatus.Completed, priorTask.Status);
        var identity = $"prior/{priorTask.Id.Value}/verification-output";
        var expected = Encoding.UTF8.GetBytes(WorkerVerificationEvidence.ProjectPriorTaskOutputForContext(priorTask));
        var handoffBytes = fixture.WritePointer(identity, expected, "unused-materialization.bin");

        var result = fixture.Prepare();

        fixture.AssertDeliveredBytes(result, identity, expected);
        Assert.Equal(handoffBytes, File.ReadAllBytes(fixture.HandoffPath));
        Assert.False(File.Exists(fixture.AbsolutePath("unused-materialization.bin")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SameGoalPrior_RetainedPointerHashMismatch_Rejects(bool malformedHash)
    {
        using var fixture = new PreparationFixture(CreateTempDirectory(), includePrior: true);
        fixture.RecordPriorVerification();
        var priorTask = fixture.Goal.Tasks[0];
        Assert.NotEqual(WorkTaskStatus.Completed, priorTask.Status);
        var identity = $"prior/{priorTask.Id.Value}/verification-output";
        var expected = Encoding.UTF8.GetBytes(WorkerVerificationEvidence.ProjectPriorTaskOutputForContext(priorTask));
        var pointer = LegacyHandoffCompatibilityResolver.CreateV1Pointer(new LogicalArtifactIdentity(identity), expected);
        var actualHash = WorkerContextArtifact.Hash(expected);
        File.WriteAllText(fixture.HandoffPath,
            PreparationFixture.PointerPrefix + pointer.Replace(actualHash,
                malformedHash ? "invalid-hash" : new string('0', 64), StringComparison.Ordinal));

        var error = Assert.Throws<WorkerContextPreparationException>(() => fixture.Prepare());

        Assert.Equal(identity, error.Identity.Value);
        Assert.Equal("authoritative-evidence-hash-mismatch", error.Reason);
        Assert.Null(fixture.Task.LastDispatch);
    }

    [Fact]
    public void FirstPlanner_SelfPointer_DeliversExactIdentityAndHash()
    {
        using var fixture = new PreparationFixture(CreateTempDirectory());
        var identity = $"prior/{fixture.Task.Id.Value}/verification-output";
        var expected = Encoding.UTF8.GetBytes("required Planner self evidence");
        var relativePath = $".orchestrator-context/legacy-handoff/{fixture.Task.Id.Value}/verification-output.bin";
        var materialization = fixture.WriteMaterialization(relativePath, expected);
        var handoffBytes = fixture.WritePointer(identity, expected, relativePath);
        Assert.Single(fixture.Goal.Tasks);
        Assert.Equal(AgentRole.Planner, fixture.Task.RequiredRole);
        Assert.Null(fixture.Task.LastVerification);

        var result = fixture.Prepare();

        fixture.AssertDeliveredBytes(result, identity, expected);
        Assert.Equal(handoffBytes, File.ReadAllBytes(fixture.HandoffPath));
        Assert.Equal(expected, File.ReadAllBytes(materialization));
    }

    [Theory]
    [InlineData(AgentRole.Developer, false)]
    [InlineData(AgentRole.Developer, true)]
    [InlineData(AgentRole.Planner, false)]
    [InlineData(AgentRole.Planner, true)]
    public void SelfPointer_MissingOrTampered_RejectsWithExistingReason(AgentRole role, bool tampered)
    {
        using var fixture = new PreparationFixture(CreateTempDirectory(), role: role);
        var identity = $"prior/{fixture.Task.Id.Value}/verification-output";
        var expected = Encoding.UTF8.GetBytes("required current task bytes");
        var relativePath = $".orchestrator-context/legacy-handoff/{fixture.Task.Id.Value}/verification-output.bin";
        var handoffBytes = fixture.WritePointer(identity, expected, relativePath);
        if (tampered)
        {
            fixture.WriteMaterialization(relativePath, Encoding.UTF8.GetBytes("tampered current task bytes"));
        }

        var error = Assert.Throws<WorkerContextPreparationException>(() => fixture.Prepare());

        Assert.Equal(identity, error.Identity.Value);
        Assert.Equal("authoritative-evidence-missing", error.Reason);
        Assert.Equal(handoffBytes, File.ReadAllBytes(fixture.HandoffPath));
        Assert.Null(fixture.Task.LastDispatch);
    }

    private sealed class PreparationFixture : IDisposable
    {
        internal const string PointerPrefix = "Compatibility pointer (v1, hash-bound; resolve from authoritative task evidence): ";
        private readonly string _root;
        private static readonly DateTimeOffset PreparedAt = DateTimeOffset.Parse("2026-10-28T12:00:00Z");

        public PreparationFixture(string root, AgentRole role = AgentRole.Planner, bool includePrior = false)
        {
            _root = root;
            WorkingDirectory = Path.Combine(root, "repo");
            Directory.CreateDirectory(WorkingDirectory);
            Task = new TaskSpec(TaskId.New(), "Prepare current goal context.", role);
            Kernel = new AgentOrchestratorKernel();
            Goal = Kernel.CreateGoal("Prepare from current graph evidence", includePrior
                ? [new TaskSpec(TaskId.New(), "Earlier current-goal task.", AgentRole.Planner), Task]
                : [Task]);
            Kernel.ActivateGoal(Goal.Id, AgentCatalog.Default().Agents);
        }

        public AgentOrchestratorKernel Kernel { get; }
        public Goal Goal { get; }
        public TaskSpec Task { get; }
        public string WorkingDirectory { get; }
        public string HandoffPath => Path.Combine(WorkingDirectory, ".orchestrator-handoff.md");
        public string AbsolutePath(string relativePath) =>
            Path.Combine(WorkingDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar));

        public void RecordPriorVerification(int exitCode = 1) => Kernel.RecordTaskVerification(Goal.Id, Goal.Tasks[0].Id,
            new TaskVerificationRecord("prior verification", WorkingDirectory, exitCode, "current goal retained output",
                string.Empty, PreparedAt, FullStandardOutput: "current goal retained output", FullStandardError: string.Empty));

        public byte[] WritePointer(string identity, byte[] bytes, string relativePath)
        {
            var pointer = LegacyHandoffCompatibilityResolver.CreateV1Pointer(
                new LogicalArtifactIdentity(identity), bytes, relativePath);
            var handoff = Encoding.UTF8.GetBytes("# Prior Task Handoff\r\n" + PointerPrefix + pointer + "\r\n");
            File.WriteAllBytes(HandoffPath, handoff);
            return handoff;
        }

        public string WriteMaterialization(string relativePath, byte[] bytes)
        {
            var path = AbsolutePath(relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, bytes);
            return path;
        }

        public WorkerProfileDispatchResult Prepare() => WorkerProfileDispatcher.PrepareTask(
            Kernel, Goal, Task,
            new WorkerProfile("codex-cli", "codex exec --sandbox {sandboxMode} --cd {workingDirectory}"),
            Path.Combine(_root, "prompts"), WorkingDirectory, PreparedAt,
            providerName: "OpenAI", modelName: AgentCatalog.OpenAiSolSubscriptionModelAlias);

        public void AssertDeliveryExcludes(WorkerProfileDispatchResult result, params string[] excluded)
        {
            var receipt = Assert.IsType<WorkerContextPackageReceipt>(result.Task.LastDispatch!.ContextPackageReceipt);
            Assert.NotEmpty(receipt.SemanticPackageId);
            Assert.NotEmpty(receipt.Sections);
            Assert.Contains(receipt.Sections, section => section.LogicalIdentity == "brief/current.md");
            var prompt = File.ReadAllText(result.PromptPath);
            foreach (var value in excluded)
            {
                Assert.DoesNotContain(value, prompt, StringComparison.Ordinal);
                Assert.DoesNotContain(receipt.Sections, section => section.LogicalIdentity.Contains(value, StringComparison.Ordinal));
            }

            foreach (var section in receipt.Sections.Where(section => section.MandatoryRelativePath is not null))
            {
                var bytes = File.ReadAllBytes(AbsolutePath(section.MandatoryRelativePath!));
                Assert.Equal(section.ContentHash, WorkerContextArtifact.Hash(bytes));
                foreach (var value in excluded)
                {
                    Assert.DoesNotContain(value, Encoding.UTF8.GetString(bytes), StringComparison.Ordinal);
                }
            }
        }

        public void AssertDeliveredBytes(WorkerProfileDispatchResult result, string identity, byte[] expected)
        {
            var section = Assert.Single(result.Task.LastDispatch!.ContextPackageReceipt!.Sections,
                candidate => candidate.LogicalIdentity == identity);
            Assert.Equal(WorkerContextArtifact.Hash(expected), section.ContentHash);
            Assert.Equal(expected, File.ReadAllBytes(AbsolutePath(section.MandatoryRelativePath!)));
        }

        public void Dispose() => Directory.Delete(_root, recursive: true);
    }
}
