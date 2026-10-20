using System.Text;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Application;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using static ConductorDriverTests;

internal sealed class DeferredNoChangeContextFixture : IDisposable
{
    private readonly string _fixtureDirectory = ConductorDriverTests.CreateTempDirectory();
    internal string Root => Path.Combine(_fixtureDirectory, "repository");
    private string DataRoot => Path.Combine(_fixtureDirectory, "data");
    internal AgentOrchestratorKernel Kernel { get; private set; }
    internal Goal Goal => Kernel.Goals.Single();
    internal TaskSpec Developer => Goal.Tasks.Single(task => task.RequiredRole == AgentRole.Developer);
    internal TaskSpec TesterA => Goal.Tasks.First(task => task.RequiredRole == AgentRole.Tester);
    internal string Worktree { get; }
    internal string Candidate { get; }
    internal OrchestratorWorkspace Workspace { get; }
    internal string Store => Path.Combine(Workspace.OrchestratorDirectory, "pre-review-evidence-attempts");
    internal string GoalStore => Path.Combine(Store, Goal.Id.Value);
    internal ConductorParallelAcceptanceAttemptCoordinator Reader => new(Store, TrunkBranchName.Default,
        runInline: true, acquireStableSlotLease: (_, _) => null,
        buildStorageRoot: new DotnetBuildStorageRoot(Path.Combine(DataRoot, "build-storage")));
    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    internal DeferredNoChangeContextFixture()
    {
        try
        {
            var created = SoftwareGoal("Deliver deferred execution to the next Tester");
            Kernel = created.Kernel;
            var goal = created.Goal;
            Worktree = GoalWorktrees.WorktreePath(Root, goal.Id);
            var project = Path.Combine(Root, "tests", "Mcg.AgentOrchestrator.Infrastructure.Tests");
            Directory.CreateDirectory(project);
            File.WriteAllText(Path.Combine(project, "Mcg.AgentOrchestrator.Infrastructure.Tests.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
            File.WriteAllText(Path.Combine(project, "DeferredAlphaTests.cs"), "public class DeferredAlphaTests {}\n");
            GitIn(Root, "init", "-b", TrunkBranchName.Default);
            GitIn(Root, "add", "tests");
            GitIn(Root, "-c", "user.name=Fixture", "-c", "user.email=fixture@example.invalid", "commit", "-m", "fixture");
            GitIn(Root, "worktree", "add", "-b", GoalWorktrees.BranchName(goal.Id), Worktree);
            Candidate = Git("rev-parse", "HEAD");
            Workspace = OrchestratorWorkspace.ForProject("delivery-fixture", Root, executionDirectory: Root,
                dataRootDirectory: DataRoot);
            Assert.Equal(Worktree, Workspace.ResolveExecutionDirectory(goal.Id));
            Directory.CreateDirectory(Workspace.OrchestratorDirectory);
            Directory.CreateDirectory(GoalStore);
            Kernel.SetGoalRefinedSpec(goal.Id, new RefinedSpec("Deliver evidence", ["Tester sees execution"], VerificationClass.TestVerifiable, [], []));
            foreach (var task in goal.Tasks.TakeWhile(task => task.Id != Developer.Id)) PassVerification(Kernel, goal, task);
            Kernel.RetryTask(goal.Id, Developer.Id, "Verify unchanged candidate.");
            DispatchTask(Kernel, goal, Developer, command: "fixture", workingDirectory: Worktree, baseCommit: Candidate);
            const string rationale = "NO_CHANGE: candidate already contains the fix.";
            Kernel.RecordDispatchExecutionResult(goal.Id, Developer.Id, new TaskVerificationRecord("fixture", Worktree, 0,
                rationale + "\nWORKER_RESULT:\nfiles: none\ncommands: none\ntests: deferred - DeferredAlphaTests\ncommit: none\nblockers: none\nassigned_scope_complete: true\nmodel_fit: test/model - adequate - fixture\nskills: none\nconfidence: high\nEND_WORKER_RESULT",
                new DeferredNoChangeOutcome(Candidate, ["DeferredAlphaTests"], rationale).FormatMarker(),
                DateTimeOffset.UtcNow, WorkerResultPresent: true, HasCommittedChanges: false));
            Assert.Equal("deferred-no-change-round", Developer.LastVerification!.CompletionVerdictRule);
        }
        catch (Exception setupError)
        {
            try { Dispose(); }
            catch (Exception cleanupError) { throw new AggregateException("Fixture setup and cleanup failed.", setupError, cleanupError); }
            throw;
        }
    }

    internal void Complete(IReadOnlyList<string>? executedClasses = null)
    {
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            getPreReviewEvidenceContext: _ => NoPreReviewContext(Candidate),
            focusedEvidenceAttemptCoordinator: Reader,
            runFocusedEvidence: (_, request) =>
            {
                var source = ConductorDriverTestsFindingEvidenceReuse.RetainedEvidenceWithExecutedClasses(GoalStore, request, Candidate, executedClasses);
                return source with { Arms = [source.Arms!.Single(arm => arm.Arm == FindingEvidenceArm.Candidate)] };
            },
            dispatchAndStart: _ => DispatchStartOutcome.Started(),
            recordFindingEvidenceRequest: (goal, task, message) => Kernel.RecordFindingEvidenceRequest(goal, task, message),
            recordFindingEvidenceRun: (goal, task, message) => Kernel.RecordFindingEvidenceRun(goal, task, message),
            executionDirectory: Root); // The driver resolves the goal worktree from the main repository.
        driver.OverrideDeveloperCompletionStructuralPreflightForTests(
            _ => new DeveloperCompletionStructuralFindings(false, "clean"), TimeSpan.FromSeconds(5));
        driver.AdvanceOnce(Goal, ConductorAutonomyPolicy.Conservative);
        Assert.Equal("green", Locator.Outcome);
    }

    internal DeferredNoChangeEvidenceEntry Locator => Assert.IsType<DeferredNoChangeEvidenceEntry>(
        DeferredNoChangeEvidenceIndexLines.Latest(Goal, Developer.Id, Candidate));
    internal string MetadataPath
    {
        get
        {
            var resultPath = Assert.IsType<string>(Locator.ResultPath);
            Assert.EndsWith(".result.json", resultPath, StringComparison.Ordinal);
            return resultPath[..^".result.json".Length] + ".attempt.json";
        }
    }
    internal ConductorParallelAcceptanceAttempt Attempt => JsonSerializer.Deserialize<ConductorParallelAcceptanceAttempt>(File.ReadAllText(MetadataPath), JsonOptions)!;
    internal ConductorParallelAcceptanceRunArtifact Result => JsonSerializer.Deserialize<ConductorParallelAcceptanceRunArtifact>(File.ReadAllText(Locator.ResultPath!), JsonOptions)!;
    internal void WriteAttempt(ConductorParallelAcceptanceAttempt attempt) => File.WriteAllText(attempt.MetadataPath, JsonSerializer.Serialize(attempt, JsonOptions));
    internal void WriteResult(ConductorParallelAcceptanceRunArtifact result) => File.WriteAllText(Locator.ResultPath!, JsonSerializer.Serialize(result, JsonOptions));
    internal void Mark(DeferredNoChangeEvidenceEntry entry) => Kernel.RecordFindingEvidenceRun(Goal.Id, TesterA.Id, DeferredNoChangeEvidenceIndexLines.FormatMarker(entry));
    internal DeferredNoChangeEvidenceContext Capture(string? candidate = null) => Reader.CaptureDeferredEvidenceContext(Goal, candidate ?? Candidate);
    internal async Task Reload()
    {
        var path = Workspace.SqliteStatePath;
        await new SqliteOrchestratorStateRepository(path).SaveAsync(Kernel);
        Kernel = await new SqliteOrchestratorStateRepository(path).LoadGoalsAsync([Goal.Id]);
    }

    internal TaskSpec AddTester() => Kernel.AddTask(Goal.Id, AgentRole.Tester, "Inspect completed evidence", DefaultAgents(), beforeRole: AgentRole.Reviewer);

    internal WorkerProfileDispatchResult Prepare(TaskSpec task)
    {
        new SqliteOrchestratorStateRepository(Workspace.SqliteStatePath).SaveAsync(Kernel).GetAwaiter().GetResult();
        return new GoalDispatchOperations(resolveHome: _ => OrchestratorHome.ForInstallRoot(Path.Combine(DataRoot, "install")))
            .ProfileDispatchTask(Kernel, Workspace, Goal, task, new WorkerProfile("fixture", "worker --cd {workingDirectory}"),
                sandboxOptions: new WorkerSandboxOptions(false, WorkerSandboxOptions.DefaultAccount, WorkerSandboxOptions.DefaultCredentialTarget));
    }

    internal WorkerContextPackage Package(TaskSpec? task = null, DeferredNoChangeEvidenceContext? context = null, string? candidateSha = null)
    {
        task ??= TesterA;
        var directory = Path.Combine(DataRoot, "context");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "artifact-registry.json"), "{\"artifacts\":[]}");
        return WorkerProfileDispatcher.BuildContextPackage(Goal, task, Worktree, directory,
            new(Goal.Id, task.Id, task.RequiredRole, "Evidence", "Inspect evidence."),
            currentCandidateSha: candidateSha ?? Candidate, deferredEvidence: context ?? Capture(candidateSha));
    }

    internal static JsonElement Summary(WorkerContextPackage package) => JsonSerializer.Deserialize<JsonElement>(
        Assert.Single(package.Artifacts, artifact => artifact.Identity.Value == "goal/deferred-no-change-evidence.json").AuthoritativeBytes!);

    internal static byte[] Recover(string worktree, WorkerContextSectionReceipt section) =>
        File.ReadAllBytes(Path.Combine(worktree, section.MandatoryRelativePath!));

    internal string Git(params string[] arguments) => GitIn(Worktree, arguments);

    private static string GitIn(string workingDirectory, params string[] arguments)
    {
        var result = GitCli.Run(workingDirectory, arguments);
        Assert.True(result.Succeeded, result.Error);
        return result.Output.Trim();
    }

    public void Dispose() => SharedTestSupport.RemoveTempDirectory(_fixtureDirectory);
}
