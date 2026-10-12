using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: all stores, worktrees and build leases use test-owned temporary roots;
// console output is captured by the AsyncLocal router, and lifecycle execution is fake.
public sealed class LifecycleGoalEpicInheritanceTests : CliCommandTestBase
{
    [Theory]
    [InlineData("lifecycle-goal")]
    [InlineData("lifecycle-simple-goal")]
    public void LinkedBacklog_DefersInheritanceUntilCommit(string command)
    {
        using var fixture = new Fixture(command);
        var (portfolio, epic, item) = fixture.SeedEpic();
        var output = CaptureConsole(() => Assert.True(CliCommandHandlers.Execute(
            Parts(command, item.Id), fixture.Context)));

        var goal = Assert.Single(fixture.Context.Kernel.Goals);
        Assert.Equal(item.Id, goal.SourceBacklogItemId);
        Assert.Equal(GoalStatus.Completed, goal.Status);
        Assert.Equal(1, Assert.IsType<FakeAcceptanceVerifier>(fixture.Context.AcceptanceVerifier).RunCount);
        var action = Assert.Single(fixture.Actions);
        Assert.Null(portfolio.GetGoalMembershipAsync(goal.Id.Value).GetAwaiter().GetResult());
        Assert.DoesNotContain("Assigned goal", output);

        var assigned = CaptureConsole(action);

        Assert.Equal(epic.Id, portfolio.GetGoalMembershipAsync(goal.Id.Value).GetAwaiter().GetResult()!.EpicId);
        Assert.Contains($"Assigned goal {goal.Id.Value[..8]} to epic {epic.Id[..8]}", assigned);
    }

    [Theory]
    [InlineData("lifecycle-goal")]
    [InlineData("lifecycle-simple-goal")]
    public void ExplicitEpic_OverridesBacklogAtCreation(string command)
    {
        using var fixture = new Fixture(command);
        var (portfolio, inherited, item) = fixture.SeedEpic();
        var requested = portfolio.AddEpicAsync("Requested board").GetAwaiter().GetResult();
        var parts = Parts(command, item.Id);
        parts.AddRange(["--epic", requested.Title]);

        var output = CaptureConsole(() => Assert.True(CliCommandHandlers.Execute(parts, fixture.Context)));

        var goal = Assert.Single(fixture.Context.Kernel.Goals);
        var notice = Assert.Single(Lines(output).Where(line => line.StartsWith("Explicit epic ", StringComparison.Ordinal)));
        Assert.Contains(requested.Id[..8], notice);
        Assert.Contains(inherited.Id[..8], notice);
        Assert.Null(portfolio.GetGoalMembershipAsync(goal.Id.Value).GetAwaiter().GetResult());
        CaptureConsole(Assert.Single(fixture.Actions));
        Assert.Equal(requested.Id, portfolio.GetGoalMembershipAsync(goal.Id.Value).GetAwaiter().GetResult()!.EpicId);
    }

    [Theory]
    [InlineData("lifecycle-goal", false)]
    [InlineData("lifecycle-simple-goal", false)]
    [InlineData("lifecycle-goal", true)]
    [InlineData("lifecycle-simple-goal", true)]
    public void UnresolvedEpic_RejectsBeforeCreation(string command, bool ambiguous)
    {
        using var fixture = new Fixture(command);
        var portfolio = new PortfolioStore(fixture.Context.Workspace.PortfolioStorePath);
        if (ambiguous)
        {
            portfolio.AddEpicAsync("Duplicate board").GetAwaiter().GetResult();
            portfolio.AddEpicAsync("Duplicate board").GetAwaiter().GetResult();
        }
        var parts = Parts(command);
        parts.AddRange(["--epic", ambiguous ? "Duplicate board" : "Missing board"]);

        CaptureConsole(() => Assert.Throws<InvalidOperationException>(() =>
            CliCommandHandlers.Execute(parts, fixture.Context)));

        Assert.Empty(fixture.Context.Kernel.Goals);
        Assert.Empty(fixture.Actions);
    }

    [Theory]
    [InlineData("lifecycle-goal", false)]
    [InlineData("lifecycle-simple-goal", false)]
    [InlineData("lifecycle-goal", true)]
    [InlineData("lifecycle-simple-goal", true)]
    public void NoEpic_DoesNotRegisterOrCreatePortfolio(string command, bool linked)
    {
        using var fixture = new Fixture(command);
        var item = linked
            ? new BacklogStore(fixture.Context.Workspace.BacklogStorePath).AddAsync("Source without epic").GetAwaiter().GetResult()
            : null;
        Assert.False(File.Exists(fixture.Context.Workspace.PortfolioStorePath));

        var output = CaptureConsole(() => Assert.True(CliCommandHandlers.Execute(
            Parts(command, item?.Id), fixture.Context)));

        var goal = Assert.Single(fixture.Context.Kernel.Goals);
        Assert.Equal(item?.Id, goal.SourceBacklogItemId);
        Assert.Empty(fixture.Actions);
        // No membership can exist without its store; constructing PortfolioStore here
        // would create that file and invalidate the absent-store control.
        Assert.False(File.Exists(fixture.Context.Workspace.PortfolioStorePath));
        Assert.DoesNotContain("Assigned goal", output);
    }

    [Theory]
    [InlineData("lifecycle-goal")]
    [InlineData("lifecycle-simple-goal")]
    public void DirectContext_AssignsImmediatelyWithoutRegistrar(string command)
    {
        using var fixture = new Fixture(command, registerActions: false);
        var (portfolio, epic, item) = fixture.SeedEpic();

        var output = CaptureConsole(() => Assert.True(CliCommandHandlers.Execute(
            Parts(command, item.Id), fixture.Context)));

        var goal = Assert.Single(fixture.Context.Kernel.Goals);
        Assert.Empty(fixture.Actions);
        Assert.Equal(epic.Id, portfolio.GetGoalMembershipAsync(goal.Id.Value).GetAwaiter().GetResult()!.EpicId);
        Assert.Contains("Assigned goal", output);
    }

    [Theory]
    [InlineData("lifecycle-goal")]
    [InlineData("lifecycle-simple-goal")]
    public void ReusedGoal_DoesNotRegisterAnotherAssignment(string command)
    {
        using var fixture = new Fixture(command);
        var (portfolio, epic, item) = fixture.SeedEpic();
        var parts = Parts(command, item.Id);
        CaptureConsole(() => Assert.True(CliCommandHandlers.Execute(parts, fixture.Context)));
        var goal = Assert.Single(fixture.Context.Kernel.Goals);
        CaptureConsole(Assert.Single(fixture.Actions));
        fixture.Actions.Clear();
        fixture.Context.Worktrees.Remove(fixture.Root, goal.Id, fixture.Context.Kernel);
        GoalOperationJournal.Completed(fixture.Root, goal, "workspace:remove", "Fake workspace removed.");
        var requested = portfolio.AddEpicAsync("Another board").GetAwaiter().GetResult();
        parts.AddRange(["--epic", requested.Title]);

        var output = CaptureConsole(() => Assert.True(CliCommandHandlers.Execute(parts, fixture.Context)));

        Assert.Same(goal, Assert.Single(fixture.Context.Kernel.Goals));
        Assert.Empty(fixture.Actions);
        Assert.Contains("reused existing idempotent goal", output);
        Assert.Equal(epic.Id, portfolio.GetGoalMembershipAsync(goal.Id.Value).GetAwaiter().GetResult()!.EpicId);
        Assert.DoesNotContain("Explicit epic ", output);

        var invalid = Parts(command, item.Id);
        invalid.AddRange(["--epic", "Missing board"]);
        CaptureConsole(() => Assert.Throws<InvalidOperationException>(() =>
            CliCommandHandlers.Execute(invalid, fixture.Context)));
        Assert.Same(goal, Assert.Single(fixture.Context.Kernel.Goals));
        Assert.Empty(fixture.Actions);
    }

    [Theory]
    [InlineData("lifecycle-goal")]
    [InlineData("lifecycle-simple-goal")]
    public void ReadinessFailure_RollsBackGoalAndSkipsEpicAssignment(string command)
    {
        using var fixture = new Fixture(command);
        RunGit(fixture.Root, "init", "-b", "main");
        RunGit(fixture.Root, "config", "user.email", "tests@example.invalid");
        RunGit(fixture.Root, "config", "user.name", "Lifecycle tests");
        File.WriteAllText(Path.Combine(fixture.Root, "seed.txt"), "seed");
        RunGit(fixture.Root, "add", "seed.txt");
        RunGit(fixture.Root, "commit", "-m", "Seed");
        var workspace = CreateRefinedWorkspace(fixture.Root);
        var (portfolio, _, item) = fixture.SeedEpic();
        var repository = CreateMigratedStateRepository(workspace.SqliteStatePath);
        Assert.Empty(repository.LoadAsync().GetAwaiter().GetResult().Goals);
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var profiles = WorkerProfileCatalog.Default();
        Goal? current = null;
        const string objective = "Update auth token rollback policy in src/Mcg.AgentOrchestrator.App/AuthPolicy.cs";
        var parts = Parts(command, item.Id, objective);

        var output = CaptureConsole(() =>
        {
            var error = Assert.Throws<InvalidOperationException>(() => CliPersistentStateRunner.ExecuteCommand(
                parts, repository, workspace, ref agents, new InMemoryModelProviderRegistry([]), ref profiles, ref current));
            Assert.Contains("--confirm-readiness-risk", error.Message);
        });

        const string prefix = "Lifecycle goal: ";
        var createdLine = Assert.Single(Lines(output).Where(line => line.StartsWith(prefix, StringComparison.Ordinal)));
        var createdId = createdLine[prefix.Length..];
        Assert.True(Guid.TryParseExact(createdId, "N", out _), createdLine);
        Assert.Contains("Stage workspace create:", output);
        Assert.DoesNotContain("Stage run-goal:", output);
        Assert.DoesNotContain("Assigned goal", output);
        var persisted = new SqliteOrchestratorStateRepository(workspace.SqliteStatePath).LoadAsync().GetAwaiter().GetResult();
        Assert.DoesNotContain(persisted.Goals, goal => goal.Objective == objective || goal.Id.Value == createdId);
        Assert.Null(portfolio.GetGoalMembershipAsync(createdId).GetAwaiter().GetResult());
    }

    [Theory]
    [InlineData("lifecycle-goal")]
    [InlineData("lifecycle-simple-goal")]
    public void Flags_AcceptEpicAndRejectUnknownOptions(string command)
    {
        CliCommandHelp.ThrowIfInvalidFlags([command, "--epic", "x"]);
        var error = Assert.Throws<ArgumentException>(() =>
            CliCommandHelp.ThrowIfInvalidFlags([command, "--zz-not-a-real-flag"]));
        Assert.Contains("Unknown option '--zz-not-a-real-flag'", error.Message);
        using var fixture = new Fixture(command);
        var output = CaptureConsole(() => CliCommandHandlers.Execute([command, "--help"], fixture.Context));
        Assert.Contains($"Usage: {command} <objective>", output);
        Assert.Contains("[--epic <id-or-title>]", output);
    }

    private static List<string> Parts(string command, string? itemId = null, string objective = "Ship a small echo change")
    {
        var parts = new List<string> { command, objective, "--confirm-batch-start", "--confirm-large-paid-subscription-start" };
        if (itemId is not null)
            parts.AddRange(["--backlog-item", itemId[..8], "--backlog-coverage", "full"]);
        return parts;
    }

    private static string[] Lines(string output) => output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = CreateTempDirectory();
        public List<Action> Actions { get; } = [];
        public CliExecutionContext Context { get; }

        public Fixture(string command, bool registerActions = true)
        {
            SeedLocalSkillCatalog(Root);
            Context = CreateLifecycleContext(Root, new FakeGoalWorktreeService(Root), FakeAcceptanceVerifier.Passed(),
                fiveRole: command == "lifecycle-goal", registerPostCommitAction: registerActions ? Actions.Add : null);
        }

        public (PortfolioStore Portfolio, PortfolioEpic Epic, BacklogItem Item) SeedEpic()
        {
            var portfolio = new PortfolioStore(Context.Workspace.PortfolioStorePath);
            var epic = portfolio.AddEpicAsync("Inherited board").GetAwaiter().GetResult();
            var item = new BacklogStore(Context.Workspace.BacklogStorePath).AddAsync("Source item").GetAwaiter().GetResult();
            portfolio.AssignBacklogItemToEpicAsync(item.Id, epic.Id).GetAwaiter().GetResult();
            return (portfolio, epic, item);
        }

        public void Dispose() => SharedTestSupport.RemoveTempDirectory(Root);
    }

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

    private static CliExecutionContext CreateLifecycleContext(
        string root,
        FakeGoalWorktreeService worktrees,
        FakeAcceptanceVerifier verifier,
        bool fiveRole = false,
        Action<Action>? registerPostCommitAction = null)
    {
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        // Direct contexts need the same migrated WAL state database as the persistent runner.
        _ = StateDbMigrations.EnsureUpToDate(workspace.SqliteStatePath);
        var kernel = new AgentOrchestratorKernel();
        var agents = fiveRole ? EchoAgents() : [EchoDeveloper()];
        return new CliExecutionContext(kernel, workspace, SeedSpecRefiner(workspace), agents, EchoProfiles(), null, registerPostCommitAction: registerPostCommitAction)
        {
            AcceptanceVerifier = verifier,
            Worktrees = worktrees,
            // Fake acceptance uses a test-owned lease instead of the process-wide slot pool.
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

    }

    private static void DeleteDirectory(string path) => SharedTestSupport.RemoveTempDirectory(path);
}
