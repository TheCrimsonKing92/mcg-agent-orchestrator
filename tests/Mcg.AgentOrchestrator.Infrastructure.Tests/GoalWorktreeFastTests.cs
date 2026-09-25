using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class GoalWorktreeTests
{
    private static AgentDefinition EchoDeveloper() => new(
        new AgentId("echo-developer"),
        "Echo Developer",
        AgentRole.Developer,
        new ModelProfile("OpenAI", "gpt-4o-mini", ModelCapability.Text, SubscriptionMode.ApiKey),
        ExecutionPolicy: AgentExecutionPolicy.SubscriptionOnly,
        Subscription: new SubscriptionLaunchProfile("local"));

    private static WorkerProfileCatalog EchoProfiles() => new(
    [
        new WorkerProfile("local", "Write-Output {subscriptionModelName}")
    ]);

    [Xunit.Fact(DisplayName = "Cli_lifecycle_simple_goal_runs_accepts_and_defers_workspace_cleanup")]
    public void CliLifecycleSimpleGoalRunsAcceptsAndDefersWorkspaceCleanup()
    {
        var root = CreateTempDirectory();
        try
        {
            var worktrees = new FakeGoalWorktreeService(root);
            var verifier = FakeAcceptanceVerifier.Passed();
            var context = CreateLifecycleContext(root, worktrees, verifier);

            var output = CaptureConsole(() => CliCommandHandlers.Execute(
                ["lifecycle-simple-goal", "Ship a small echo change", "--confirm-batch-start", "--confirm-large-paid-subscription-start"],
                context));

            var goal = context.CurrentGoal!;
            Xunit.Assert.Equal(GoalStatus.Completed, goal.Status);
            Xunit.Assert.NotNull(worktrees.TryResolve(root, goal.Id));
            Xunit.Assert.Equal(1, verifier.RunCount);
            Xunit.Assert.Equal(1, worktrees.EnsureCount);
            Xunit.Assert.Equal(1, worktrees.MergeCount);
            Xunit.Assert.Equal(0, worktrees.RemoveCount);
            Xunit.Assert.Contains("Stage workspace cleanup:", output);
            Xunit.Assert.Contains("Workspace cleanup deferred", output);
            AssertNoBuildArtifacts(root);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Xunit.Fact(DisplayName = "Cli_lifecycle_goal_runs_five_role_goal_accepts_and_defers_workspace_cleanup")]
    public void CliLifecycleGoalRunsFiveRoleGoalAcceptsAndDefersWorkspaceCleanup()
    {
        var root = CreateTempDirectory();
        try
        {
            var worktrees = new FakeGoalWorktreeService(root);
            var verifier = FakeAcceptanceVerifier.Passed();
            var context = CreateLifecycleContext(root, worktrees, verifier, fiveRole: true);

            CliCommandHandlers.Execute(
                ["lifecycle-goal", "Ship a five-role echo change", "--pipeline", "five-role", "--confirm-batch-start", "--confirm-large-paid-subscription-start"],
                context);

            var goal = context.CurrentGoal!;
            Xunit.Assert.Equal(GoalStatus.Completed, goal.Status);
            Xunit.Assert.Equal(5, goal.Tasks.Count);
            Xunit.Assert.NotNull(worktrees.TryResolve(root, goal.Id));
            Xunit.Assert.Equal(1, verifier.RunCount);
            Xunit.Assert.Equal(1, worktrees.MergeCount);
            Xunit.Assert.Equal(0, worktrees.RemoveCount);
            AssertNoBuildArtifacts(root);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Xunit.Fact(DisplayName = "Cli_lifecycle_simple_goal_keeps_workspace_when_acceptance_fails")]
    public void CliLifecycleSimpleGoalKeepsWorkspaceWhenAcceptanceFails()
    {
        var root = CreateTempDirectory();
        try
        {
            var worktrees = new FakeGoalWorktreeService(root);
            var verifier = FakeAcceptanceVerifier.Failed("Focused tests failed");
            var context = CreateLifecycleContext(root, worktrees, verifier);

            var output = CaptureConsole(() =>
            {
                var ex = Xunit.Assert.ThrowsAny<InvalidOperationException>(() => CliCommandHandlers.Execute(
                    ["lifecycle-simple-goal", "Run but fail acceptance", "--confirm-batch-start", "--confirm-large-paid-subscription-start"],
                    context));
                Xunit.Assert.Contains("acceptance", ex.Message);
            });

            var goal = context.CurrentGoal!;
            Xunit.Assert.Equal(GoalStatus.Verified, goal.Status);
            Xunit.Assert.NotNull(worktrees.TryResolve(root, goal.Id));
            Xunit.Assert.Equal(1, verifier.RunCount);
            Xunit.Assert.Equal(0, worktrees.MergeCount);
            Xunit.Assert.Equal(0, worktrees.RemoveCount);
            Xunit.Assert.Contains("merge blocked", output);
            AssertNoBuildArtifacts(root);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Xunit.Fact(DisplayName = "Cli_lifecycle_simple_goal_keeps_workspace_when_acceptance_throws")]
    public void CliLifecycleSimpleGoalKeepsWorkspaceWhenAcceptanceThrows()
    {
        var root = CreateTempDirectory();
        try
        {
            var worktrees = new FakeGoalWorktreeService(root);
            var verifier = FakeAcceptanceVerifier.Throws(new InvalidOperationException("fake verifier boom"));
            var context = CreateLifecycleContext(root, worktrees, verifier);

            var ex = Xunit.Assert.ThrowsAny<InvalidOperationException>(() => CliCommandHandlers.Execute(
                ["lifecycle-simple-goal", "Run but verifier throws", "--confirm-batch-start", "--confirm-large-paid-subscription-start"],
                context));

            var goal = context.CurrentGoal!;
            Xunit.Assert.Equal("fake verifier boom", ex.Message);
            Xunit.Assert.Equal(GoalStatus.Verified, goal.Status);
            Xunit.Assert.NotNull(worktrees.TryResolve(root, goal.Id));
            Xunit.Assert.Equal(1, verifier.RunCount);
            Xunit.Assert.Equal(0, worktrees.MergeCount);
            Xunit.Assert.Equal(0, worktrees.RemoveCount);
            AssertNoBuildArtifacts(root);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Xunit.Fact(DisplayName = "Cli_lifecycle_simple_goal_keeps_workspace_when_acceptance_times_out")]
    public void CliLifecycleSimpleGoalKeepsWorkspaceWhenAcceptanceTimesOut()
    {
        var root = CreateTempDirectory();
        try
        {
            var worktrees = new FakeGoalWorktreeService(root);
            var verifier = FakeAcceptanceVerifier.Timeout();
            var context = CreateLifecycleContext(root, worktrees, verifier);

            var output = CaptureConsole(() =>
            {
                var ex = Xunit.Assert.ThrowsAny<InvalidOperationException>(() => CliCommandHandlers.Execute(
                    ["lifecycle-simple-goal", "Run but time out acceptance", "--confirm-batch-start", "--confirm-large-paid-subscription-start"],
                    context));
                Xunit.Assert.Contains("acceptance", ex.Message);
            });

            var goal = context.CurrentGoal!;
            Xunit.Assert.Equal(GoalStatus.Verified, goal.Status);
            Xunit.Assert.NotNull(worktrees.TryResolve(root, goal.Id));
            Xunit.Assert.Equal(1, verifier.RunCount);
            Xunit.Assert.Equal(0, worktrees.MergeCount);
            Xunit.Assert.Equal(0, worktrees.RemoveCount);
            Xunit.Assert.Contains("merge blocked", output);
            AssertNoBuildArtifacts(root);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    private static CliExecutionContext CreateLifecycleContext(
        string root,
        FakeGoalWorktreeService worktrees,
        FakeAcceptanceVerifier verifier,
        bool fiveRole = false)
    {
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var kernel = new AgentOrchestratorKernel();
        var agents = fiveRole ? EchoAgents() : [EchoDeveloper()];
        return new CliExecutionContext(kernel, workspace, SeedSpecRefiner(workspace), agents, EchoProfiles(), null)
        {
            AcceptanceVerifier = verifier,
            Worktrees = worktrees,
            // These lifecycle tests run inside the acceptance suite's stable build slot.
            // Keep the fake verifier isolated from the process-wide slot pool so a nested
            // slot collision cannot bypass it and turn RunCount assertions nondeterministic.
            StableSlotSelector = (_, _) => CreateFakeStableSlotLease(root),
            RunGoalPollInterval = TimeSpan.FromMilliseconds(1),
            RunGoalSleep = (_, _) => Task.CompletedTask,
            RunGoalOverride = goal =>
            {
                foreach (var task in goal.Tasks)
                {
                    kernel.RecordTaskVerification(
                        goal.Id,
                        task.Id,
                        ManualVerificationRecorder.Create(true, "Fake lifecycle run-goal passed.", worktrees.TryResolve(root, goal.Id)!, DateTimeOffset.UtcNow));
                }

                return Task.FromResult(new RunGoalService.RunGoalResult(
                    Executed: true,
                    StopReason: "Goal completed.",
                    BlockingAction: null,
                    CompletedTasks: goal.Tasks
                        .Select(task => new RunGoalService.RunGoalTaskSummary(
                            TaskDisplayNumber.Resolve(goal, task.Id),
                            task.Id.Value,
                            task.Description,
                            Succeeded: true,
                            OutputTail: null))
                        .ToList(),
                    StopEvidence: null));
            }
        };
    }

    private static DotnetBuildEnvironmentLease CreateFakeStableSlotLease(string root)
    {
        var leaseRoot = Path.Combine(root, ".fake-build-slot");
        Directory.CreateDirectory(leaseRoot);
        var lockPath = Path.Combine(leaseRoot, $"{Guid.NewGuid():N}.lock");
        var stream = new FileStream(lockPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        var environment = new DotnetBuildEnvironment(
            "fake-lifecycle-slot",
            leaseRoot,
            Path.Combine(leaseRoot, "artifacts"),
            lockPath,
            [],
            "build-0",
            BuildPermitIndex: 0);
        return new DotnetBuildEnvironmentLease(environment, stream);
    }

    private static IReadOnlyList<AgentDefinition> EchoAgents() =>
    [
        EchoAgent(AgentRole.Planner),
        EchoAgent(AgentRole.Researcher),
        EchoDeveloper(),
        EchoAgent(AgentRole.Tester),
        EchoAgent(AgentRole.Reviewer)
    ];

    private static AgentDefinition EchoAgent(AgentRole role) => new(
        new AgentId($"echo-{role.ToString().ToLowerInvariant()}"),
        $"Echo {role}",
        role,
        new ModelProfile("OpenAI", "gpt-4o-mini", ModelCapability.Text, SubscriptionMode.ApiKey),
        ExecutionPolicy: AgentExecutionPolicy.SubscriptionOnly,
        Subscription: new SubscriptionLaunchProfile("local"));

    private static InMemoryModelProviderRegistry SeedSpecRefiner(OrchestratorWorkspace workspace)
    {
        ModelFunctionCatalogStore.Save(workspace.ModelFunctionCatalogPath, new ModelFunctionCatalog([
            new ModelFunctionBinding(
                ModelFunctionPurposes.SpecRefiner,
                ModelLane.CheapApi,
                new ModelProfile("fake-refiner", "fake-model", ModelCapability.Text, SubscriptionMode.ApiKey))
        ]));
        return new InMemoryModelProviderRegistry([
            new FakeSmokeProvider("{}", providerName: "fake-refiner")
        ]);
    }

    private sealed class FakeGoalWorktreeService(string root) : ICliGoalWorktreeService
    {
        private readonly Dictionary<GoalId, string> worktrees = [];

        public int EnsureCount { get; private set; }

        public int MergeCount { get; private set; }

        public int RemoveCount { get; private set; }

        public string BranchName(GoalId goalId) => $"goal/{goalId.Value[..8]}";

        public string Ensure(string executionDirectory, GoalId goalId)
        {
            EnsureCount++;
            if (!worktrees.TryGetValue(goalId, out var path))
            {
                path = Path.Combine(root, ".fake-worktrees", goalId.Value[..8]);
                Directory.CreateDirectory(path);
                worktrees[goalId] = path;
            }

            return path;
        }

        public string? TryResolve(string executionDirectory, GoalId goalId) =>
            worktrees.TryGetValue(goalId, out var path) ? path : null;

        public GoalWorktreeRemoveResult Remove(
            string executionDirectory,
            GoalId goalId,
            AgentOrchestratorKernel? kernel = null,
            int? gitTimeoutMilliseconds = null)
        {
            RemoveCount++;
            if (worktrees.Remove(goalId, out var path))
            {
                DeleteDirectory(path);
            }

            return new GoalWorktreeRemoveResult("Removed fake worktree.", null, [], null);
        }

        public GoalWorktreeRemoveResult RemoveTerminalNow(
            string executionDirectory,
            GoalId goalId,
            AgentOrchestratorKernel kernel) =>
            Remove(executionDirectory, goalId, kernel);

        public bool IsGitWorkTree(string executionDirectory) => true;

        public GoalWorktreeMergeResult? TryFastForwardMerge(string executionDirectory, GoalId goalId)
        {
            MergeCount++;
            return TryResolve(executionDirectory, goalId) is null
                ? null
                : new GoalWorktreeMergeResult(true, BranchName(goalId), $"Fast-forwarded {BranchName(goalId)}.", null);
        }

        public GoalWorktreeRebaseResult TryRebaseOntoMain(string executionDirectory, GoalId goalId) =>
            new(GoalWorktreeRebaseStatus.AlreadyFastForwardable, BranchName(goalId), "Already current.", [], null);

        public bool NeedsRebaseOntoMain(string executionDirectory, GoalId goalId) => false;

        public bool IsWorktreeClean(string executionDirectory, GoalId goalId) => true;

        public bool HasChangesAgainstMain(string executionDirectory, GoalId goalId) => true;

        public string ResolveHead(string worktreePath) => "fake-head";

        public IReadOnlyList<string> GetChangedFiles(string worktreePath) => ["src/fake-lifecycle-change.cs"];

        public GoalAcceptanceEvidenceBundle BuildAcceptanceEvidence(
            AgentOrchestratorKernel kernel,
            Goal goal,
            string? worktreePath,
            AcceptanceVerificationResult? verification,
            bool verificationSkipped,
            string? executionDirectory = null)
        {
            var changedFiles = GetChangedFiles(worktreePath ?? root);
            var changeSummary = RepositoryChangeClassifier.Classify(changedFiles);
            var policy = VerificationPolicyCompiler.Compile(
                AgentRole.Reviewer,
                goal.Objective,
                string.Join(Environment.NewLine, goal.Tasks.Select(task => task.Description)),
                string.Join(Environment.NewLine, goal.Tasks.Select(task => task.VerificationPlan)),
                changedFiles);
            var acceptanceChecks = verification?.Checks?.ToArray() ?? [];
            var policyChecks = policy.Checks
                .Select(check => new GoalAcceptancePolicyCheckEvidence(
                    check.Name,
                    check.Kind,
                    check.Required,
                    acceptanceChecks.Any(result => result.Name.Equals(check.Name, StringComparison.OrdinalIgnoreCase) && result.Passed)
                        ? "passed"
                        : "not-required",
                    check.CommandLine,
                    check.Reason))
                .ToArray();
            var blockers = verification is { Passed: false }
                ? [new GoalAcceptanceEvidenceBlocker("acceptance-verification-failed", "Acceptance verification failed.", $"acceptance {goal.Id.Value[..8]}")]
                : Array.Empty<GoalAcceptanceEvidenceBlocker>();

            return new GoalAcceptanceEvidenceBundle(
                goal.Id,
                goal.Objective,
                goal.Status,
                worktreePath,
                verificationSkipped || (verification?.Passed == true && blockers.Length == 0),
                verificationSkipped,
                changedFiles,
                "fake diff stat",
                changeSummary,
                RepositoryTestImpactPlanner.Plan(changeSummary),
                policy,
                policyChecks,
                new GoalAcceptanceBuildEnvironmentEvidence(goal.Id.Value, root, Path.Combine(root, "lease.json"), true, false),
                acceptanceChecks,
                [],
                blockers,
                blockers.Select(blocker => blocker.SuggestedCommand).ToArray());
        }
    }

    private sealed class FakeAcceptanceVerifier(
        AcceptanceVerificationResult? result,
        Exception? exception = null) : IGoalAcceptanceVerifier
    {
        public int RunCount { get; private set; }

        public Task<AcceptanceVerificationResult> RunOwnedAsync(
            string worktreePath,
            GoalId? goalId,
            IReadOnlyList<string>? changedFiles,
            int? stableSlotIndex,
            DotnetBuildEnvironmentLease? stableSlotLease,
            IAcceptanceAttemptExecutionOwner executionOwner)
        {
            RunCount++;
            if (exception is not null)
                throw exception;

            var configuredResult = Xunit.Assert.IsType<AcceptanceVerificationResult>(result);
            return Task.FromResult(AddPolicyRequiredChecks(configuredResult, changedFiles ?? []));
        }

        public Task<FocusedEvidenceRunResult> RunFocusedEvidenceOwnedAsync(
            string worktreePath,
            GoalId? goalId,
            string request,
            IAcceptanceFocusedVerificationOwner executionOwner,
            int? stableSlotIndex = null,
            DotnetBuildEnvironmentLease? stableSlotLease = null,
            bool runBaselineArm = false)
        {
            if (exception is not null)
                throw exception;

            return Task.FromResult(new FocusedEvidenceRunResult(
                request,
                Accepted: true,
                Passed: true,
                Summary: "focused evidence passed",
                Checks: []));
        }

        private static AcceptanceVerificationResult AddPolicyRequiredChecks(
            AcceptanceVerificationResult result,
            IReadOnlyList<string> changedFiles)
        {
            if (!result.Passed)
                return result;

            var checks = result.Checks?.ToList() ?? [];
            var existing = checks
                .Select(check => check.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var policy = VerificationPolicyCompiler.Compile(
                AgentRole.Reviewer,
                goalObjective: string.Empty,
                taskDescription: string.Empty,
                verificationPlan: null,
                changedFiles);
            foreach (var check in policy.Checks.Where(check =>
                check.Required &&
                !check.Kind.StartsWith("manual", StringComparison.OrdinalIgnoreCase) &&
                existing.Add(check.Name)))
            {
                checks.Add(new AcceptanceCheckResult(check.Name, true, 0, null));
            }

            return result with { Checks = checks };
        }

        public static FakeAcceptanceVerifier Passed() =>
            new(new AcceptanceVerificationResult(
                Passed: true,
                Skipped: false,
                ExitCode: 0,
                OutputTail: null,
                Checks: [new AcceptanceCheckResult("fake acceptance", true, 0, null)]));

        public static FakeAcceptanceVerifier Failed(string outputTail) =>
            new(new AcceptanceVerificationResult(
                Passed: false,
                Skipped: false,
                ExitCode: 1,
                OutputTail: outputTail,
                Checks: [new AcceptanceCheckResult("fake acceptance", false, 1, outputTail)]));

        public static FakeAcceptanceVerifier Timeout() =>
            new(new AcceptanceVerificationResult(
                Passed: false,
                Skipped: false,
                ExitCode: -1,
                OutputTail: "Verification command timed out after elapsed=25m budget=25m.\nCommand: fake acceptance\nLast output:\nstill running",
                Checks:
                [
                    new AcceptanceCheckResult(
                        "fake acceptance",
                        false,
                        -1,
                        "Verification command timed out after elapsed=25m budget=25m.\nCommand: fake acceptance\nLast output:\nstill running")
                ]));

        public static FakeAcceptanceVerifier Throws(Exception exception) => new(null, exception);
    }

    private static void AssertNoBuildArtifacts(string root)
    {
        var buildArtifactDirectories = Directory
            .EnumerateDirectories(root, "*", SearchOption.AllDirectories)
            .Where(path =>
                Path.GetFileName(path).Equals("bin", StringComparison.OrdinalIgnoreCase) ||
                Path.GetFileName(path).Equals("obj", StringComparison.OrdinalIgnoreCase) ||
                Path.GetFileName(path).Equals("TestResults", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        Xunit.Assert.Empty(buildArtifactDirectories);
    }

    private static string CreateTempDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "mcg-worktree-fast-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void DeleteDirectory(string path)
    {
        if (!Directory.Exists(path))
            return;

        Directory.Delete(path, recursive: true);
    }
}
