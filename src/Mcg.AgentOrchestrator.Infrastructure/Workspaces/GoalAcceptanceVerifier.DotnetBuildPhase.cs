using System.Diagnostics;
using static Mcg.AgentOrchestrator.Infrastructure.AcceptanceCheckCommandBuilder;
using static Mcg.AgentOrchestrator.Infrastructure.AcceptancePolicyShardPlanner;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal sealed record DotnetBaseBuildCachePlan(
    string MainSha,
    IReadOnlyList<string> RestoreProjects,
    IReadOnlyList<string> BuildProjects,
    IReadOnlyList<string> CacheableProjects);

internal sealed class DotnetTestBuildPhase(string[] buildArguments, DotnetBaseBuildCachePlan? cachePlan)
{
    public string[] BuildArguments { get; } = buildArguments;
    public DotnetBaseBuildCachePlan? CachePlan { get; } = cachePlan;
    public DotnetBuildEnvironment? BuildEnvironment { get; set; }
    private (AcceptanceCheckResult Result, bool Retried)? _run;
    public TaskCompletionSource BuildCompleted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public (AcceptanceCheckResult Result, bool Retried)? Run
    {
        get => _run;
        set
        {
            _run = value;
            if (value is not null)
                BuildCompleted.TrySetResult();
        }
    }
}

internal readonly record struct DotnetTestBuildPhaseResult(
    (AcceptanceCheckResult Result, bool Retried) Run,
    bool ContributesToCheck);

internal sealed record AcceptanceDotnetBuildPhaseHooks(
    Func<string[], string, Action<DotnetBuildEnvironment>?, DotnetBuildEnvironment?, Task<(AcceptanceCheckResult Result, bool Retried)>> RunManagedCheckAsync,
    Func<string, DotnetBuildEnvironment> ResolveExecutionEnvironment,
    Func<string, string> AttemptSlug,
    Func<DotnetBaseBuildCache> ResolveBaseBuildCache);

internal sealed class AcceptanceDotnetBuildPhase
{
    private static readonly string[] CacheableProjects =
    [
        CoreProject,
        ProvidersProject,
        OperatorCommsProject,
        InfrastructureProject,
        AppProject,
        CoreTestsProject,
        InfrastructureTestsProject,
        AcceptanceTestsProject,
        ExecutionTestsProject,
        TestSupportProject,
        ProviderEnvironmentTestsProject,
        CliTestsProject
    ];

    internal static DotnetTestBuildPhase Create(
        string worktreePath,
        IReadOnlyList<BuildPhaseManifestCheck> checks,
        IReadOnlyList<string>? changedFiles,
        PolicyShardPlan policyShardPlan,
        Func<string, string?> resolveMainSha)
    {
        var dotnetTestChecks = checks
            .Where(check => check.Type.Equals("dotnet-test", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var cacheableProjects = CacheableProjects
            .Where(project =>
                (!ProjectMatches(project, AcceptanceTestsProject) &&
                 !ProjectMatches(project, ExecutionTestsProject)) ||
                dotnetTestChecks.Any(check => ProjectMatches(check.Project, project)))
            .ToArray();
        DotnetBaseBuildCachePlan? cachePlan = TryCreateBaseBuildCachePlan(
            worktreePath,
            changedFiles,
            policyShardPlan,
            cacheableProjects,
            resolveMainSha);
        var solutionCheck = dotnetTestChecks.FirstOrDefault(check =>
            !string.IsNullOrWhiteSpace(check.Project) &&
            check.Project.EndsWith(".sln", StringComparison.OrdinalIgnoreCase));
        if (solutionCheck is not null)
        {
            return new DotnetTestBuildPhase(BuildDotnetTestBuildArguments(solutionCheck), cachePlan);
        }

        if (TryFindRootSolution(worktreePath) is { } solutionPath)
        {
            var template = dotnetTestChecks.FirstOrDefault();
            var templateArguments = template?.Arguments ?? [];
            return new DotnetTestBuildPhase(BuildDotnetTestBuildArguments(
                ["dotnet", "test", solutionPath, .. templateArguments]), cachePlan);
        }

        var firstCheck = dotnetTestChecks.FirstOrDefault();
        return new DotnetTestBuildPhase(firstCheck is null
            ? ["dotnet", "build"]
            : BuildDotnetTestBuildArguments(firstCheck), cachePlan);
    }

    private static DotnetBaseBuildCachePlan? TryCreateBaseBuildCachePlan(
        string worktreePath,
        IReadOnlyList<string>? changedFiles,
        PolicyShardPlan policyShardPlan,
        string[] cacheableProjects,
        Func<string, string?> resolveMainSha)
    {
        if (changedFiles is null ||
            changedFiles.Count == 0 ||
            !policyShardPlan.Applies ||
            policyShardPlan.ForceFull)
        {
            return null;
        }

        var buildProjects = policyShardPlan.DependencyClosure
            .Where(project => cacheableProjects.Contains(project, StringComparer.OrdinalIgnoreCase))
            .OrderBy(project => Array.IndexOf(cacheableProjects, project))
            .ToArray();
        if (buildProjects.Length == 0 || buildProjects.Length == cacheableProjects.Length)
        {
            return null;
        }

        var mainSha = resolveMainSha(worktreePath);
        if (string.IsNullOrWhiteSpace(mainSha))
        {
            return null;
        }

        var restoreProjects = cacheableProjects
            .Where(project => !buildProjects.Contains(project, StringComparer.OrdinalIgnoreCase))
            .ToArray();
        return restoreProjects.Length == 0
            ? null
            : new DotnetBaseBuildCachePlan(mainSha, restoreProjects, buildProjects, cacheableProjects);
    }

    private static string? TryFindRootSolution(string worktreePath)
    {
        var preferred = Path.Combine(worktreePath, "Mcg.AgentOrchestrator.sln");
        if (File.Exists(preferred))
        {
            return Path.GetFileName(preferred);
        }

        try
        {
            return Directory.EnumerateFiles(worktreePath, "*.sln", SearchOption.TopDirectoryOnly)
                .Select(Path.GetFileName)
                .FirstOrDefault(name => !string.IsNullOrWhiteSpace(name));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            return null;
        }
    }

    internal static async Task<DotnetTestBuildPhaseResult> EnsureAsync(
        DotnetTestBuildPhase phase,
        string attemptName,
        AcceptanceDotnetBuildPhaseHooks hooks)
    {
        if (phase.Run is { } completed)
        {
            if (phase.BuildEnvironment is null)
            {
                throw new InvalidOperationException(
                    "A completed acceptance build phase has no recorded build environment.");
            }

            return new DotnetTestBuildPhaseResult(completed, ContributesToCheck: false);
        }

        phase.BuildEnvironment ??= hooks.ResolveExecutionEnvironment($"{attemptName}-build");
        if (phase.CachePlan is not null &&
            await TryRunCachedAsync(
                phase,
                attemptName,
                hooks).ConfigureAwait(false) is { } cachedRun)
        {
            phase.Run = cachedRun;
            return new DotnetTestBuildPhaseResult(cachedRun, ContributesToCheck: true);
        }

        phase.Run = await hooks.RunManagedCheckAsync(
            phase.BuildArguments,
            $"{attemptName}-build",
            null,
            phase.BuildEnvironment).ConfigureAwait(false);
        return new DotnetTestBuildPhaseResult(phase.Run.Value, ContributesToCheck: true);
    }

    private static async Task<(AcceptanceCheckResult Result, bool Retried)?> TryRunCachedAsync(
        DotnetTestBuildPhase phase,
        string attemptName,
        AcceptanceDotnetBuildPhaseHooks hooks)
    {
        var plan = phase.CachePlan;
        if (plan is null)
        {
            return null;
        }

        var wall = Stopwatch.StartNew();
        var environment = hooks.ResolveExecutionEnvironment($"{attemptName}-cache");
        var cache = hooks.ResolveBaseBuildCache();
        var restore = cache.Probe(plan.MainSha, plan.RestoreProjects);
        var retried = false;
        var lockRemediationApplied = false;
        (AcceptanceCheckResult Result, bool Retried)? lastRun = null;
        IReadOnlyList<string> builtProjects;
        DotnetBaseBuildCachePublishResult? publish = null;

        if (restore.AllHit)
        {
            builtProjects = plan.BuildProjects;
            var restoredIntoPreparedSlot = false;
            foreach (var project in plan.BuildProjects)
            {
                var projectRun = await hooks.RunManagedCheckAsync(
                    BuildDotnetProjectBuildArguments(project, phase.BuildArguments),
                    $"{attemptName}-build-{hooks.AttemptSlug(ProjectLabel(project))}",
                    preparedEnvironment =>
                    {
                        if (restoredIntoPreparedSlot)
                        {
                            return;
                        }

                        restore = cache.Restore(plan.MainSha, preparedEnvironment.ArtifactsPath, plan.RestoreProjects);
                        restoredIntoPreparedSlot = true;
                    }, null).ConfigureAwait(false);
                retried |= projectRun.Retried;
                lockRemediationApplied |= projectRun.Result.LockRemediationApplied;
                lastRun = projectRun;
                if (!projectRun.Result.Passed)
                {
                    break;
                }
            }
        }
        else
        {
            builtProjects = plan.CacheableProjects;
            lastRun = await hooks.RunManagedCheckAsync(
                phase.BuildArguments,
                $"{attemptName}-build",
                null,
                null).ConfigureAwait(false);
            retried |= lastRun.Value.Retried;
            lockRemediationApplied |= lastRun.Value.Result.LockRemediationApplied;
            if (lastRun.Value.Result.Passed)
            {
                publish = cache.Publish(plan.MainSha, environment.ArtifactsPath, plan.RestoreProjects);
            }
        }

        wall.Stop();
        if (lastRun is null)
        {
            return null;
        }

        var receiptSummary = BuildBaseBuildCacheSummary(
            plan,
            restore,
            publish,
            builtProjects,
            (long)wall.Elapsed.TotalMilliseconds);
        EmitBaseBuildCacheReceipt(receiptSummary);
        return (lastRun.Value.Result with
        {
            DurationMilliseconds = (long)wall.Elapsed.TotalMilliseconds,
            LockRemediationApplied = lockRemediationApplied,
            ResultSummary = PrefixResultSummary(receiptSummary, lastRun.Value.Result.ResultSummary)
        }, retried);
    }

    private static string BuildBaseBuildCacheSummary(
        DotnetBaseBuildCachePlan plan,
        DotnetBaseBuildCacheRestoreResult restore,
        DotnetBaseBuildCachePublishResult? publish,
        IReadOnlyList<string> builtProjects,
        long buildPhaseMilliseconds)
    {
        var projectReceipts = plan.CacheableProjects
            .Select(project =>
            {
                var restored = restore.Projects.FirstOrDefault(receipt =>
                    receipt.Project.Equals(project, StringComparison.OrdinalIgnoreCase));
                if (restored is not null)
                {
                    return $"{ProjectLabel(project)}={restored.Status}";
                }

                var published = publish?.Projects.FirstOrDefault(receipt =>
                    receipt.Project.Equals(project, StringComparison.OrdinalIgnoreCase));
                return published is not null
                    ? $"{ProjectLabel(project)}=miss,published"
                    : $"{ProjectLabel(project)}=changed";
            });
        var evictions = restore.Evictions.Concat(publish?.Evictions ?? []).ToArray();
        return
            $"base-build-cache main_sha={plan.MainSha} build_phase_ms={buildPhaseMilliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture)} " +
            $"projects={string.Join(",", projectReceipts)} " +
            $"built_projects={string.Join(",", builtProjects.Select(ProjectLabel))} " +
            $"evictions={(evictions.Length == 0 ? "none" : string.Join(",", evictions))}";
    }

    private static void EmitBaseBuildCacheReceipt(string summary)
    {
        Console.WriteLine($"BASE_BUILD_CACHE {summary}");
        Console.Out.Flush();
    }

    private static string[] BuildDotnetProjectBuildArguments(string project, string[] templateBuildArguments)
    {
        var args = new List<string> { "dotnet", "build", project };
        var startIndex = templateBuildArguments.Length > 2 && !templateBuildArguments[2].StartsWith("-", StringComparison.Ordinal)
            ? 3
            : 2;
        for (var index = startIndex; index < templateBuildArguments.Length; index++)
        {
            var argument = templateBuildArguments[index];
            if (!IsBuildCompatibleDotnetArgument(argument))
            {
                if (ArgumentExpectsValue(argument))
                {
                    index++;
                }

                continue;
            }

            args.Add(argument);
            if (ArgumentExpectsValue(argument) && index + 1 < templateBuildArguments.Length)
            {
                args.Add(templateBuildArguments[++index]);
            }
        }

        return [.. args];
    }

    internal static string PrefixResultSummary(string prefix, string? summary) =>
        string.IsNullOrWhiteSpace(summary)
            ? prefix
            : $"{prefix}; {summary}";
}
