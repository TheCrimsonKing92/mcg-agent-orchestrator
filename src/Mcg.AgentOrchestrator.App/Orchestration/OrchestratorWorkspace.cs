using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

public sealed record OrchestratorWorkspace(
    string RootDirectory,
    string ExecutionDirectory,
    string OrchestratorDirectory,
    string ProjectName,
    bool IsProjectScoped,
    string TenantName,
    bool IsTenantScoped,
    string SqliteStatePath,
    string AgentCatalogPath,
    string WorkerProfilePath,
    string PromptDirectory,
    string LogDirectory,
    string ContinuationStorePath,
    string TranscriptPath)
{
    public const string DefaultProjectName = "default";
    public string? ProjectHomeDirectoryOrNull => IsProjectScoped ? OrchestratorDirectory : null;
    public string IntegrationBranch { get; init; } = TrunkBranchName.Default;
    public string ConductorStopFilePath => Path.Combine(
        IsProjectScoped ? OrchestratorDirectory : ExecutionDirectory, ConductorBatchLoop.StopFileName);
    public const string DefaultTenantName = "default";
    public const string ContinuationStoreFileName = "continuation-watches.json";
    public const string RepoRootEnvironmentVariable = "MCG_ORCHESTRATOR_REPOSITORY_ROOT";
    private const string GitDirectoryName = ".git";

    // Configuration wins for deployed layouts where the orchestrator binary is
    // outside the target repository tree.
    public static string ResolveRepoRoot(string? startDirectory = null, string? fallbackDirectory = null)
    {
        var configuredRoot = Environment.GetEnvironmentVariable(RepoRootEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(configuredRoot))
        {
            return Path.GetFullPath(configuredRoot);
        }

        var candidates = new[]
        {
            startDirectory ?? Environment.CurrentDirectory,
            fallbackDirectory ?? AppContext.BaseDirectory
        };

        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate))
                continue;

            var dir = new DirectoryInfo(Path.GetFullPath(candidate));
            while (dir != null)
            {
                if (Directory.Exists(Path.Combine(dir.FullName, GitDirectoryName)))
                    return dir.FullName;
                dir = dir.Parent;
            }
        }

        return startDirectory ?? Environment.CurrentDirectory;
    }

    public static OrchestratorWorkspace ForDirectory(
        string rootDirectory,
        string? executionDirectory = null,
        string? tenantName = null) =>
        ForDirectory(rootDirectory, executionDirectory, tenantName, OrchestratorProjectRegistry.CreateDefault());

    public static OrchestratorWorkspace ForDirectory(
        string rootDirectory,
        string? executionDirectory,
        string? tenantName,
        OrchestratorProjectRegistry registry)
    {
        var root = Path.GetFullPath(rootDirectory);
        var executionRoot = Path.GetFullPath(string.IsNullOrWhiteSpace(executionDirectory) ? rootDirectory : executionDirectory);
        var normalizedTenant = OrchestratorTenantSelection.NormalizeTenantName(tenantName);
        var isTenantScoped = !normalizedTenant.Equals(DefaultTenantName, StringComparison.OrdinalIgnoreCase);
        var orchestrator = ResolveOrchestratorDirectory(root, null, normalizedTenant);
        var location = registry.GetDefaultStateLocation(root);
        if (location is not null)
        {
            var stateRoot = location.ResolveStateDirectory(registry.DataRootDirectory);
            orchestrator = isTenantScoped ? Path.Combine(stateRoot, "tenants", normalizedTenant) : stateRoot;
        }
        return Create(
            root,
            executionRoot,
            orchestrator,
            DefaultProjectName,
            false,
            normalizedTenant,
            isTenantScoped);
    }

    public static OrchestratorWorkspace ForProject(
        string projectName,
        string rootDirectory,
        string? executionDirectory = null,
        string? tenantName = null,
        string? integrationBranch = null,
        string? dataRootDirectory = null)
    {
        var normalizedProject = OrchestratorProjectSelection.NormalizeProjectName(projectName);
        if (normalizedProject.Equals(DefaultProjectName, StringComparison.OrdinalIgnoreCase))
        {
            return ForDirectory(rootDirectory, executionDirectory, tenantName) with
            {
                IntegrationBranch = TrunkBranchName.Resolve(integrationBranch)
            };
        }

        var root = Path.GetFullPath(rootDirectory);
        var executionRoot = Path.GetFullPath(string.IsNullOrWhiteSpace(executionDirectory) ? rootDirectory : executionDirectory);
        var normalizedTenant = OrchestratorTenantSelection.NormalizeTenantName(tenantName);
        var isTenantScoped = !normalizedTenant.Equals(DefaultTenantName, StringComparison.OrdinalIgnoreCase);
        var dataRoot = dataRootDirectory is null
            ? OrchestratorDataRoot.Resolve()
            : OrchestratorDataRoot.FromDirectory(dataRootDirectory);
        var projectDirectory = dataRoot.ProjectDirectory(normalizedProject);
        if (IsWithinDirectory(projectDirectory, root) || IsWithinDirectory(projectDirectory, executionRoot))
            throw new InvalidOperationException("Project data root must be outside the target repository.");
        var orchestrator = normalizedTenant.Equals(DefaultTenantName, StringComparison.OrdinalIgnoreCase)
            ? projectDirectory
            : Path.Combine(projectDirectory, "tenants", normalizedTenant);
        return Create(
            root,
            executionRoot,
            orchestrator,
            normalizedProject,
            true,
            normalizedTenant,
            isTenantScoped) with { IntegrationBranch = TrunkBranchName.Resolve(integrationBranch) };
    }

    private static string ResolveOrchestratorDirectory(string root, string? projectName, string tenantName)
    {
        var baseDirectory = string.IsNullOrWhiteSpace(projectName)
            ? Path.Combine(root, ".orchestrator")
            : Path.Combine(root, ".orchestrator", "projects", projectName);
        return tenantName.Equals(DefaultTenantName, StringComparison.OrdinalIgnoreCase)
            ? baseDirectory
            : Path.Combine(baseDirectory, "tenants", tenantName);
    }

    internal static string LegacyProjectDirectory(string rootDirectory, string projectName) =>
        Path.Combine(Path.GetFullPath(rootDirectory), ".orchestrator", "projects",
            OrchestratorProjectSelection.NormalizeProjectName(projectName));

    internal static bool IsWithinDirectory(string path, string directory)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        var fullPath = Path.GetFullPath(path);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return fullPath.Equals(root, comparison) ||
            fullPath.StartsWith(root + Path.DirectorySeparatorChar, comparison);
    }

    private static OrchestratorWorkspace Create(
        string root,
        string executionRoot,
        string orchestrator,
        string projectName,
        bool isProjectScoped,
        string tenantName,
        bool isTenantScoped)
    {
        return new OrchestratorWorkspace(
            root,
            executionRoot,
            orchestrator,
            projectName,
            isProjectScoped,
            tenantName,
            isTenantScoped,
            Path.Combine(orchestrator, "state.db"),
            Path.Combine(orchestrator, "agents.json"),
            Path.Combine(orchestrator, "workers.json"),
            Path.Combine(orchestrator, "prompts"),
            Path.Combine(orchestrator, "logs"),
            isProjectScoped || isTenantScoped
                ? Path.Combine(orchestrator, ContinuationStoreFileName)
                : Path.Combine(root, ContinuationStoreFileName),
            Path.Combine(orchestrator, "transcript.md"));
    }

    public string GoalLifecycleEventsDirectory => Path.Combine(OrchestratorDirectory, "goal-events");

    public string BacklogStorePath => Path.Combine(OrchestratorDirectory, "backlog.db");

    public string OperatorLessonsStorePath => Path.Combine(OrchestratorDirectory, SqliteOperatorLessonStore.DatabaseFileName);
    public string OperatorEscapesStorePath => Path.Combine(OrchestratorDirectory, SqliteOperatorEscapeStore.DatabaseFileName);

    public string PortfolioStorePath => Path.Combine(OrchestratorDirectory, "portfolio.db");
    public string ExperimentStorePath => Path.Combine(OrchestratorDirectory, "experiments.db");

    public string DogfoodLogStorePath => Path.Combine(OrchestratorDirectory, "dogfood-log.db");

    public string RunEventStorePath => Path.Combine(OrchestratorDirectory, "run-events.db");

    public string ConductEventsLogPath => Path.Combine(LogDirectory, ConductEventLogWriter.CurrentFileName);

    public string OperatorChannelPath => Path.Combine(OrchestratorDirectory, "operator-channel.json");


    // Append-only advisory log of semantic-acceptance verdicts (one JSON object per line), kept in
    // the orchestrator state directory (NOT a goal worktree) so writing a receipt never dirties an
    // acceptance diff. Source for the local-vs-subscription judge agreement comparison.
    public string SemanticAcceptanceLogPath => Path.Combine(OrchestratorDirectory, "semantic-acceptance.jsonl");

    // Registry of orchestrator-internal model functions (acceptance-judge lanes, future samplers,
    // etc.) — separate from the worker agent catalog so internal model uses never touch task routing.
    public string ModelFunctionCatalogPath => Path.Combine(OrchestratorDirectory, "model-functions.json");

    // Post-hoc worker inquiry receipts live outside goal state. They are advisory evidence only and
    // must not dirty task verification, dispatch, process, or acceptance records.
    public string InquiryReceiptDirectory => Path.Combine(OrchestratorDirectory, "inquiries");

    public string TrialComparisonReceiptDirectory => Path.Combine(OrchestratorDirectory, "trial-comparisons");

    // Answered spec-clarification forks recorded as precedents so a second goal with the same
    // forkKind reuses the recorded choice rather than re-asking.
    public string SpecRefinerPrecedentsPath => Path.Combine(OrchestratorDirectory, "spec-refiner-precedents.json");

    public string SpecRefinementLaunchAttemptsDirectory =>
        Path.Combine(OrchestratorDirectory, "spec-refinement-launch-attempts");

    // Goal work runs in the goal's worktree when one exists so concurrent
    // goals do not contend for the shared execution directory.
    public string ResolveExecutionDirectory(GoalId goalId)
    {
        return GoalWorktrees.TryResolve(ExecutionDirectory, goalId) ?? ExecutionDirectory;
    }
}
