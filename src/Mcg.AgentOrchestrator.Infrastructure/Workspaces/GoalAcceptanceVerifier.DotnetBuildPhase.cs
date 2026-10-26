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
    Func<DotnetBaseBuildCache> ResolveBaseBuildCache,
    Func<DotnetTestBuildPhase, DotnetBuildEnvironment, Task<DotnetBaseBuildCacheIdentity?>> ResolveCacheIdentityAsync);

internal sealed class AcceptanceDotnetBuildPhase
{
    internal static DotnetTestBuildPhase WithoutAnalyzers(DotnetTestBuildPhase phase)
    {
        var arguments = new List<string>(phase.BuildArguments);
        foreach (var property in AnalyzersOffBuildProperties)
            if (!arguments.Contains(property, StringComparer.Ordinal))
                arguments.Add(property);
        return new DotnetTestBuildPhase([.. arguments], phase.CachePlan);
    }

    internal static AcceptanceCheckResult FailureResult(
        BuildPhaseManifestCheck check, AcceptanceCheckResult buildResult, bool captureLimited)
    {
        var timedOut = buildResult.CompletionDecision?.TimedOut == true && !captureLimited;
        return buildResult with
        {
            Name = check.Name,
            FailureClassification = timedOut
                ? AcceptanceFailureClassifications.BuildPhaseTimeout
                : buildResult.FailureClassification,
            ResultSummary = PrefixResultSummary(
                timedOut ? "build phase timed out (build-phase budget)" : "build phase failed",
                buildResult.ResultSummary)
        };
    }

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
        var cacheableProjects = CacheEligibleProjects(
            dotnetTestChecks.Select(check => NormalizePath(check.Project)!), policyShardPlan)
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

        var buildProjects = cacheableProjects
            .Where(project => policyShardPlan.DependencyClosure.Contains(project, StringComparer.OrdinalIgnoreCase))
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
        var environment = phase.BuildEnvironment ?? throw new InvalidOperationException("Cache restoration requires an owned build environment.");
        var identity = await hooks.ResolveCacheIdentityAsync(phase, environment).ConfigureAwait(false);
        if (identity is null)
        {
            EmitBaseBuildCacheReceipt($"base-build-cache main_sha={plan.MainSha} status=miss reason=unresolved-build-inputs");
            return null;
        }
        var cache = hooks.ResolveBaseBuildCache();
        var projectMode = identity.ProjectKeys.Length > 0;
        string[] orderedProjects;
        try
        {
            orderedProjects = projectMode
                ? DotnetBaseBuildCacheProjectKeys.BuildOrder(identity.ProjectReferences, plan.CacheableProjects)
                : [.. plan.CacheableProjects];
        }
        catch (InvalidOperationException)
        {
            // An invalid evaluated graph cannot authorize builds with references disabled.
            return null;
        }
        var restoreProjects = projectMode
            ? plan.RestoreProjects.Concat(orderedProjects.Where(project => !plan.CacheableProjects.Contains(project, StringComparer.OrdinalIgnoreCase))).ToArray()
            : plan.RestoreProjects.ToArray();
        var restore = cache.Probe(plan.MainSha, restoreProjects, identity);
        var hitProjects = restore.Projects.Where(project => project.Status == "hit").Select(project => project.Project).ToArray();
        var focused = projectMode ? hitProjects.Length > 0 : restore.AllHit;
        var retried = false;
        var lockRemediationApplied = false;
        (AcceptanceCheckResult Result, bool Retried)? lastRun = null;
        IReadOnlyList<string> builtProjects = plan.BuildProjects;
        DotnetBaseBuildCachePublishResult? publish = null;

        if (focused)
        {
            builtProjects = projectMode
                ? orderedProjects.Where(project => !hitProjects.Contains(project, StringComparer.OrdinalIgnoreCase)).ToArray()
                : plan.BuildProjects;
            var restoredIntoPreparedSlot = false;
            try
            {
                foreach (var project in builtProjects)
                {
                    var arguments = BuildDotnetProjectBuildArguments(project, phase.BuildArguments);
                    if (projectMode) arguments = [.. arguments, "-p:BuildProjectReferences=false"];
                    var projectRun = await hooks.RunManagedCheckAsync(
                        arguments,
                        $"{attemptName}-build-{hooks.AttemptSlug(ProjectLabel(project))}",
                        preparedEnvironment =>
                        {
                            if (restoredIntoPreparedSlot)
                            {
                                return;
                            }

                            var restored = cache.Restore(plan.MainSha, preparedEnvironment.ArtifactsPath,
                                projectMode ? hitProjects : restoreProjects, identity);
                            restore = projectMode
                                ? new(plan.MainSha, restore.Projects.Where(receipt => !hitProjects.Contains(receipt.Project, StringComparer.OrdinalIgnoreCase))
                                    .Concat(restored.Projects).ToArray(), restore.Evictions.Concat(restored.Evictions).ToArray())
                                : restored;
                            if (!restored.AllHit)
                            {
                                cache.ClearRestoredProjects(preparedEnvironment.ArtifactsPath, restoreProjects);
                                throw new BaseBuildCacheRestoreMissException();
                            }
                            restoredIntoPreparedSlot = true;
                        }, environment).ConfigureAwait(false);
                    retried |= projectRun.Retried;
                    lockRemediationApplied |= projectRun.Result.LockRemediationApplied;
                    lastRun = projectRun;
                    if (!projectRun.Result.Passed)
                    {
                        break;
                    }
                }
            }
            catch (BaseBuildCacheRestoreMissException)
            {
                // Probe is provisional; no focused build may run on a partial restore.
                lastRun = null;
                focused = false;
            }
        }
        if (!focused)
        {
            builtProjects = projectMode ? orderedProjects : plan.CacheableProjects;
            lastRun = await hooks.RunManagedCheckAsync(
                phase.BuildArguments,
                $"{attemptName}-build",
                null,
                environment).ConfigureAwait(false);
            retried |= lastRun.Value.Retried;
            lockRemediationApplied |= lastRun.Value.Result.LockRemediationApplied;
        }
        if (lastRun?.Result.Passed == true && (!focused || projectMode))
        {
            var missedProjects = projectMode
                ? restoreProjects.Where(project => restore.Projects.Any(receipt => receipt.Project.Equals(project, StringComparison.OrdinalIgnoreCase) && receipt.Status != "hit")).ToArray()
                : restoreProjects;
            if (missedProjects.Length > 0)
            {
                // Build targets may change inputs. Re-resolve before publication rather than
                // labeling new outputs with a pre-build identity that is no longer current.
                var afterBuildIdentity = await hooks.ResolveCacheIdentityAsync(phase, environment).ConfigureAwait(false);
                if (afterBuildIdentity == identity)
                    publish = cache.Publish(plan.MainSha, environment.ArtifactsPath, missedProjects, identity, hitProjects);
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
            identity,
            (long)wall.Elapsed.TotalMilliseconds);
        EmitBaseBuildCacheReceipt(receiptSummary);
        return (lastRun.Value.Result with
        {
            DurationMilliseconds = (long)wall.Elapsed.TotalMilliseconds,
            LockRemediationApplied = lockRemediationApplied,
            ResultSummary = PrefixResultSummary(receiptSummary, lastRun.Value.Result.ResultSummary)
        }, retried);
    }

    private sealed class BaseBuildCacheRestoreMissException : Exception;

    private static string BuildBaseBuildCacheSummary(
        DotnetBaseBuildCachePlan plan,
        DotnetBaseBuildCacheRestoreResult restore,
        DotnetBaseBuildCachePublishResult? publish,
        IReadOnlyList<string> builtProjects,
        DotnetBaseBuildCacheIdentity identity,
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
        var misses = plan.CacheableProjects.Select(project => restore.Projects.FirstOrDefault(receipt =>
                receipt.Project.Equals(project, StringComparison.OrdinalIgnoreCase)))
            .Where(receipt => receipt?.Status == "miss")
            .Select(receipt => $"{ProjectLabel(receipt!.Project)}={receipt.Reason}" +
                (receipt.MismatchedComponents is null ? "" : $"[{receipt.MismatchedComponents}]"))
            .ToArray();
        var missReasons = (misses.Length == 0 ? "none" : string.Join(",", misses)).Replace(' ', '_').Replace(';', '_');
        var evictions = restore.Evictions.Concat(publish?.Evictions ?? []).ToArray();
        return
            $"base-build-cache main_sha={plan.MainSha} input_identity={identity.Fingerprint} build_phase_ms={buildPhaseMilliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture)} " +
            $"projects={string.Join(",", projectReceipts)} " +
            $"built_projects={string.Join(",", builtProjects.Select(ProjectLabel))} " +
            $"miss_reasons={missReasons} " +
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
