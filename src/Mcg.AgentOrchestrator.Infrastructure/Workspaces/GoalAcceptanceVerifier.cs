using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed record AcceptanceCheckResult(
    string Name,
    bool Passed,
    int? ExitCode,
    string? OutputTail,
    string? ArtifactsPath = null,
    string? BrokerName = null,
    string? LeaseId = null,
    long? DurationMilliseconds = null,
    bool LockRemediationApplied = false,
    string? ResultSummary = null,
    bool Advisory = false);

public sealed record AcceptanceVerificationResult(
    bool Passed,
    bool Skipped,
    int? ExitCode,
    string? OutputTail,
    bool Retried = false,
    string? ArtifactsPath = null,
    IReadOnlyList<AcceptanceCheckResult>? Checks = null);

public interface IGoalAcceptanceVerifier
{
    Task<AcceptanceVerificationResult> RunAsync(
        string worktreePath,
        GoalId? goalId = null,
        IReadOnlyList<string>? changedFiles = null,
        int? stableSlotIndex = null,
        DotnetBuildEnvironmentLease? stableSlotLease = null,
        CancellationToken cancellationToken = default);
}

public sealed class GoalAcceptanceVerifier : IGoalAcceptanceVerifier
{
    internal sealed record CommandResult(
        int ExitCode,
        string Output,
        bool TimedOut = false,
        string? CommandLine = null,
        string? StdoutPath = null,
        string? StderrPath = null,
        TimeSpan? Timeout = null,
        TimeSpan? Elapsed = null,
        TaskProcessResourceAccounting? ResourceAccounting = null);

    private static readonly Regex TestAttrPattern = new(
        @"^\[(?:Fact|Theory|Xunit\.Fact\()",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex TautologyPattern = new(
        @"Assert\.True\(\s*true\s*\)|Assert\.False\(\s*false\s*\)|Assert\.Equal\(\s*(?<v>\w+)\s*,\s*\k<v>\s*\)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly string[] DiffBaseArgs = ["git", "diff", "--unified=0", "main...HEAD", "--"];
    private const string CoreProject = "src/Mcg.AgentOrchestrator.Core/Mcg.AgentOrchestrator.Core.csproj";
    private const string InfrastructureProject = "src/Mcg.AgentOrchestrator.Infrastructure/Mcg.AgentOrchestrator.Infrastructure.csproj";
    private const string AppProject = "src/Mcg.AgentOrchestrator.App/Mcg.AgentOrchestrator.App.csproj";
    private const string CoreTestsProject = "tests/Mcg.AgentOrchestrator.Core.Tests/Mcg.AgentOrchestrator.Core.Tests.csproj";
    private const string InfrastructureTestsProject = "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj";

    private static readonly Dictionary<string, string[]> ReferencingProjectsByProject = new(StringComparer.OrdinalIgnoreCase)
    {
        [CoreProject] = [InfrastructureProject, AppProject, CoreTestsProject, InfrastructureTestsProject],
        [InfrastructureProject] = [AppProject, InfrastructureTestsProject],
        [AppProject] = [InfrastructureTestsProject],
        [CoreTestsProject] = [],
        [InfrastructureTestsProject] = []
    };

    private static readonly InfrastructureTestLane[] InfrastructureTestLanes =
    [
        new("Cli", "FullyQualifiedName~CliCommandTests"),
        new("Cli help", "FullyQualifiedName~CliHelpTests"),
        new("Worker dispatch", "FullyQualifiedName~WorkerDispatchTests"),
        new("Worker profiles", "FullyQualifiedName~WorkerProfileTests"),
        new("Worker processes", "FullyQualifiedName~WorkerProcessJobsTests"),
        new("Worker shell", "FullyQualifiedName~WorkerShellTests"),
        new("Worker sandbox planner", "FullyQualifiedName~WorkerSandboxCapabilityPlannerTests"),
        new("Dispatch process host", "FullyQualifiedName~DispatchProcessHostTests"),
        new("Goal worktree", "FullyQualifiedName~GoalWorktreeTests"),
        new("Goal acceptance verifier", "FullyQualifiedName~GoalAcceptanceVerifierTests"),
        new("Dashboard rendering", "FullyQualifiedName~DashboardRenderingTests"),
        new("Dashboard host", "FullyQualifiedName~DashboardHostTests&Category!=HostIntegration"),
        new("Dashboard validation", "FullyQualifiedName~DashboardValidationHarnessTests"),
        new("Advance loop", "FullyQualifiedName~AdvanceLoopTests"),
        new("Conductor batch loop", "FullyQualifiedName~ConductorBatchLoopTests"),
        new("Conductor driver", "FullyQualifiedName~ConductorDriverTests"),
        new("Conduct watch sweep scoping", "FullyQualifiedName~ConductWatchSweepScopingTests"),
        new("Remainder",
            "FullyQualifiedName!~CliCommandTests&FullyQualifiedName!~CliHelpTests" +
            "&FullyQualifiedName!~WorkerDispatchTests&FullyQualifiedName!~WorkerProfileTests" +
            "&FullyQualifiedName!~WorkerProcessJobsTests&FullyQualifiedName!~WorkerShellTests" +
            "&FullyQualifiedName!~WorkerSandboxCapabilityPlannerTests&FullyQualifiedName!~DispatchProcessHostTests" +
            "&FullyQualifiedName!~GoalWorktreeTests&FullyQualifiedName!~GoalAcceptanceVerifierTests" +
            "&FullyQualifiedName!~DashboardRenderingTests&FullyQualifiedName!~DashboardHostTests" +
            "&FullyQualifiedName!~DashboardValidationHarnessTests&FullyQualifiedName!~AdvanceLoopTests" +
            "&FullyQualifiedName!~ConductorBatchLoopTests&FullyQualifiedName!~ConductorDriverTests" +
            "&FullyQualifiedName!~ConductWatchSweepScopingTests&Category!=HostIntegration")
    ];

    private readonly Func<string[], string, TimeSpan, CancellationToken, Task<CommandResult>> _runner;

    public GoalAcceptanceVerifier() : this(RunProcessAsync) { }

    internal GoalAcceptanceVerifier(Func<string[], string, CancellationToken, Task<CommandResult>> runner)
        : this((arguments, workingDirectory, _, cancellationToken) =>
            runner(arguments, workingDirectory, cancellationToken))
    {
    }

    internal GoalAcceptanceVerifier(Func<string[], string, TimeSpan, CancellationToken, Task<CommandResult>> runner)
    {
        _runner = runner;
    }

    public async Task<AcceptanceVerificationResult> RunAsync(
        string worktreePath,
        GoalId? goalId = null,
        IReadOnlyList<string>? changedFiles = null,
        int? stableSlotIndex = null,
        DotnetBuildEnvironmentLease? stableSlotLease = null,
        CancellationToken cancellationToken = default)
    {
        // Shut down build servers to release file locks before running tests.
        await _runner(
            ["dotnet", "build-server", "shutdown"],
            worktreePath,
            AcceptanceCheckTimeouts.DefaultTimeout,
            cancellationToken).ConfigureAwait(false);

        var manifest = AcceptanceManifest.Load(worktreePath, changedFiles);
        var policyShardPlan = BuildPolicyShardPlan(changedFiles);

        // Apply policy-required checks from the change scope. Focused project checks replace
        // matching unfiltered project checks so a narrow App change does not still run the full
        // Infrastructure project suite from the tracked manifest.
        var policyRequiredChecks = BuildRequiredPolicyChecks(changedFiles);
        var policyEffectiveChecks = BuildPolicyEffectiveChecks(manifest.Checks, changedFiles, policyRequiredChecks, policyShardPlan);
        var effectiveChecks = ExpandBroadInfrastructureChecks(policyEffectiveChecks);

        var advisoryChecks = LoadAdvisoryChecks(worktreePath);

        var checks = new List<AcceptanceCheckResult>();
        var retried = false;

        // When the manifest has both a solution-wide dotnet-test check and granular
        // per-project .csproj checks covered by it, run the solution once and synthesize
        // results for the granular checks so the policy gate finds all required names
        // without re-running the full test suite.
        var solutionCheck = effectiveChecks.FirstOrDefault(c =>
            c.Type.Equals("dotnet-test", StringComparison.OrdinalIgnoreCase) &&
            (string.IsNullOrWhiteSpace(c.Project) || c.Project.EndsWith(".sln", StringComparison.OrdinalIgnoreCase)));

        var scopedChecks = BuildChangeScopedChecks(solutionCheck, effectiveChecks, changedFiles, policyShardPlan);
        var runSolutionCheck = solutionCheck is not null && scopedChecks is null;
        var deferredChecks = runSolutionCheck
            ? BuildDeferredChecks(solutionCheck, effectiveChecks, worktreePath)
            : [];

        if (scopedChecks is not null)
        {
            var scopedNames = scopedChecks.Select(c => c.Name).ToHashSet(StringComparer.Ordinal);

            foreach (var check in effectiveChecks.Where(c =>
                !c.Type.Equals("dotnet-test", StringComparison.OrdinalIgnoreCase)))
            {
                var checkResult = await RunCheckAsync(check, worktreePath, goalId, stableSlotIndex, stableSlotLease, cancellationToken).ConfigureAwait(false);
                retried |= checkResult.Retried;
                checks.Add(checkResult.Result);
                if (!checkResult.Result.Passed)
                {
                    break;
                }
            }

            if (checks.All(check => check.Passed))
            {
                foreach (var check in effectiveChecks.Where(c =>
                    c.Type.Equals("dotnet-test", StringComparison.OrdinalIgnoreCase) &&
                    scopedNames.Contains(c.Name)))
                {
                    var checkResult = await RunCheckAsync(check, worktreePath, goalId, stableSlotIndex, stableSlotLease, cancellationToken).ConfigureAwait(false);
                    retried |= checkResult.Retried;
                    checks.Add(checkResult.Result);
                    if (!checkResult.Result.Passed)
                    {
                        break;
                    }
                }
            }
        }
        else if (deferredChecks.Count > 0)
        {
            var deferredNames = deferredChecks.Select(c => c.Name).ToHashSet(StringComparer.Ordinal);

            var nonDotnetPassed = true;
            foreach (var check in effectiveChecks.Where(c =>
                !c.Type.Equals("dotnet-test", StringComparison.OrdinalIgnoreCase)))
            {
                var checkResult = await RunCheckAsync(check, worktreePath, goalId, stableSlotIndex, stableSlotLease, cancellationToken).ConfigureAwait(false);
                retried |= checkResult.Retried;
                checks.Add(checkResult.Result);
                if (!checkResult.Result.Passed)
                {
                    nonDotnetPassed = false;
                    break;
                }
            }

            if (nonDotnetPassed)
            {
                var slnRun = await RunCheckAsync(solutionCheck!, worktreePath, goalId, stableSlotIndex, stableSlotLease, cancellationToken).ConfigureAwait(false);
                retried |= slnRun.Retried;
                checks.Add(slnRun.Result);

                foreach (var deferred in deferredChecks)
                {
                    checks.Add(new AcceptanceCheckResult(
                        deferred.Name,
                        slnRun.Result.Passed,
                        slnRun.Result.ExitCode,
                        slnRun.Result.Passed ? null : slnRun.Result.OutputTail,
                        slnRun.Result.ArtifactsPath,
                        slnRun.Result.BrokerName,
                        slnRun.Result.LeaseId,
                        slnRun.Result.DurationMilliseconds,
                        slnRun.Retried,
                        $"covered by: {solutionCheck!.Name}"));
                }

                if (slnRun.Result.Passed)
                {
                    foreach (var check in effectiveChecks.Where(c =>
                        c.Type.Equals("dotnet-test", StringComparison.OrdinalIgnoreCase) &&
                        !c.Name.Equals(solutionCheck!.Name, StringComparison.Ordinal) &&
                        !deferredNames.Contains(c.Name)))
                    {
                        var checkResult = await RunCheckAsync(check, worktreePath, goalId, stableSlotIndex, stableSlotLease, cancellationToken).ConfigureAwait(false);
                        retried |= checkResult.Retried;
                        checks.Add(checkResult.Result);
                        if (!checkResult.Result.Passed)
                        {
                            break;
                        }
                    }
                }
            }
        }
        else
        {
            foreach (var check in effectiveChecks)
            {
                var checkResult = await RunCheckAsync(check, worktreePath, goalId, stableSlotIndex, stableSlotLease, cancellationToken).ConfigureAwait(false);
                retried |= checkResult.Retried;
                checks.Add(checkResult.Result);
                if (!checkResult.Result.Passed)
                {
                    break;
                }
            }
        }

        AddCoveredBroadInfrastructureResults(checks, policyRequiredChecks, changedFiles);
        AddCoveredBroadInfrastructureResults(checks, manifest.Checks, changedFiles);
        AddCoveredPolicyAliasResults(checks, policyRequiredChecks, effectiveChecks);
        AddPolicyShardReceiptResults(checks, manifest.Checks, effectiveChecks, policyShardPlan);

        if (checks.All(check => check.Passed) && manifest.ForbiddenChangedPathGlobs.Count > 0)
        {
            checks.Add(await RunForbiddenChangedPathsCheckAsync(manifest.ForbiddenChangedPathGlobs, worktreePath, cancellationToken).ConfigureAwait(false));
        }

        if (checks.All(check => check.Passed) && ProposalValidationApplies(worktreePath, changedFiles))
        {
            checks.Add(RunStateEffectProposalSchemaCheck(worktreePath, changedFiles));
        }

        // Advisory checks: always run, failures are recorded but do not affect overall Passed.
        foreach (var advisoryCheck in advisoryChecks)
        {
            var checkResult = await RunCheckAsync(advisoryCheck, worktreePath, goalId, stableSlotIndex, stableSlotLease, cancellationToken).ConfigureAwait(false);
            checks.Add(checkResult.Result with { Advisory = true });
        }

        var testFileChanges = changedFiles?.Where(IsTestFile).ToArray();
        if (testFileChanges is { Length: > 0 })
        {
            checks.Add(await RunTestTamperCheckAsync(testFileChanges, worktreePath, cancellationToken).ConfigureAwait(false));
        }

        var failedCheck = checks.FirstOrDefault(check => !check.Advisory && !check.Passed);
        var artifactsPath = checks.LastOrDefault(check => !string.IsNullOrWhiteSpace(check.ArtifactsPath))?.ArtifactsPath;

        return new AcceptanceVerificationResult(
            Passed: failedCheck is null,
            Skipped: false,
            ExitCode: failedCheck?.ExitCode ?? 0,
            OutputTail: failedCheck?.OutputTail,
            Retried: retried,
            ArtifactsPath: artifactsPath,
            Checks: checks);
    }

    private static List<AcceptanceManifestCheck> BuildDeferredChecks(
        AcceptanceManifestCheck? solutionCheck,
        IReadOnlyList<AcceptanceManifestCheck> allChecks,
        string worktreePath)
    {
        if (solutionCheck is null)
            return [];

        string? slnContent = null;
        if (!string.IsNullOrWhiteSpace(solutionCheck.Project) &&
            solutionCheck.Project.EndsWith(".sln", StringComparison.OrdinalIgnoreCase))
        {
            var slnPath = Path.Combine(worktreePath, solutionCheck.Project);
            if (File.Exists(slnPath))
                slnContent = File.ReadAllText(slnPath);
        }

        return allChecks
            .Where(c =>
                c.Type.Equals("dotnet-test", StringComparison.OrdinalIgnoreCase) &&
                !c.Name.Equals(solutionCheck.Name, StringComparison.Ordinal) &&
                !string.IsNullOrWhiteSpace(c.Project) &&
                c.Project.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) &&
                IsProjectInSolution(c.Project, solutionCheck.Project, slnContent))
            .ToList();
    }

    private static List<AcceptanceManifestCheck>? BuildChangeScopedChecks(
        AcceptanceManifestCheck? solutionCheck,
        IReadOnlyList<AcceptanceManifestCheck> allChecks,
        IReadOnlyList<string>? changedFiles,
        PolicyShardPlan policyShardPlan)
    {
        if (solutionCheck is null ||
            changedFiles is null ||
            changedFiles.Count == 0 ||
            policyShardPlan.ForceFull ||
            !ChangeScopedAcceptanceEnabled())
        {
            return null;
        }

        var summary = RepositoryChangeClassifier.Classify(changedFiles);
        // Do NOT gate on summary.RequiresBroadVerification: core/infra changes are escalation-broad
        // (LandingDecision still escalates them) but are test-narrow-able. Genuine full-suite cases
        // are caught by the build/security guards here and by plan.RequiresBroadVerification below.
        if (summary.HasBuildSystemChanges ||
            summary.HasSecuritySensitiveChanges)
        {
            return null;
        }

        var plan = RepositoryTestImpactPlanner.Plan(summary);
        if (!plan.RequiresBuild ||
            plan.RequiresBroadVerification ||
            plan.Checks.Any(check => check.Command.Count == 0))
        {
            return null;
        }

        var scoped = new List<AcceptanceManifestCheck>();
        foreach (var plannedCheck in plan.Checks)
        {
            var plannedManifestCheck = PolicyCheckToManifestCheck(plannedCheck);
            foreach (var scopedCheck in ExpandBroadInfrastructureCheck(plannedManifestCheck))
            {
                var existing = allChecks.FirstOrDefault(check =>
                    check.Type.Equals("dotnet-test", StringComparison.OrdinalIgnoreCase) &&
                    !check.Name.Equals(solutionCheck.Name, StringComparison.Ordinal) &&
                    DotnetCheckMatches(check, scopedCheck));
                scoped.Add(existing ?? scopedCheck);
            }
        }

        foreach (var policyShard in allChecks.Where(check => IsRunnablePolicyShardCheck(check, policyShardPlan)))
        {
            if (scoped.Any(existing => DotnetCheckMatches(existing, policyShard)))
                continue;

            scoped.Add(policyShard);
        }

        return scoped.Count == 0 ? null : scoped;
    }

    private static bool DotnetCheckMatches(AcceptanceManifestCheck left, AcceptanceManifestCheck right) =>
        string.Equals(NormalizePath(left.Project), NormalizePath(right.Project), StringComparison.OrdinalIgnoreCase) &&
        left.Arguments.SequenceEqual(right.Arguments, StringComparer.OrdinalIgnoreCase);

    private static string? NormalizePath(string? path) =>
        string.IsNullOrWhiteSpace(path) ? path : path.Replace('\\', '/').Trim();

    private static bool ChangeScopedAcceptanceEnabled()
    {
        var value = Environment.GetEnvironmentVariable("MCG_ACCEPTANCE_CHANGE_SCOPED");
        return string.IsNullOrWhiteSpace(value) ||
            value.Equals("1", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("on", StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<AcceptanceManifestCheck> BuildPolicyEffectiveChecks(
        IReadOnlyList<AcceptanceManifestCheck> manifestChecks,
        IReadOnlyList<string>? changedFiles,
        IReadOnlyList<AcceptanceManifestCheck>? policyRequiredChecks = null,
        PolicyShardPlan? policyShardPlan = null)
    {
        if (changedFiles is null || changedFiles.Count == 0)
            return manifestChecks;

        policyShardPlan ??= BuildPolicyShardPlan(changedFiles);
        var requiredPolicyChecks = policyRequiredChecks ?? BuildRequiredPolicyChecks(changedFiles);
        var plannedChecks = requiredPolicyChecks
            .Where(check => !policyShardPlan.ForceFull ||
                !check.Type.Equals("dotnet-test", StringComparison.OrdinalIgnoreCase))
            .SelectMany(ExpandBroadInfrastructureCheck)
            .ToArray();
        var focusedProjectChecks = plannedChecks
            .Where(IsFocusedProjectDotnetCheck)
            .ToArray();

        var effective = manifestChecks
            .Where(check => !IsSkippedPolicyShardCheck(check, policyShardPlan))
            .Where(check => !IsReplacedByFocusedProjectCheck(check, focusedProjectChecks))
            .ToList();
        var injectedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var plannedManifestCheck in plannedChecks)
        {
            var commandKey = ManifestCheckKey(plannedManifestCheck);
            if (effective.Any(check => ManifestCheckKey(check).Equals(commandKey, StringComparison.OrdinalIgnoreCase)))
                continue;

            if (!injectedKeys.Add(commandKey))
                continue;

            effective.Add(plannedManifestCheck);
        }

        return effective;
    }

    private static IReadOnlyList<AcceptanceManifestCheck> BuildRequiredPolicyChecks(IReadOnlyList<string>? changedFiles)
    {
        if (changedFiles is null || changedFiles.Count == 0)
            return [];

        var policy = VerificationPolicyCompiler.Compile(
            AgentRole.Reviewer,
            goalObjective: string.Empty,
            taskDescription: string.Empty,
            verificationPlan: null,
            changedFiles);
        return policy.Checks
            .Where(c =>
                c.Required &&
                (c.Kind.Equals("dotnet-test", StringComparison.OrdinalIgnoreCase) ||
                    c.Kind.Equals("browser-smoke", StringComparison.OrdinalIgnoreCase)))
            .Select(PolicyCheckToManifestCheck)
            .Where(c => c is not null)
            .Select(c => c!)
            .ToArray();
    }

    private static PolicyShardPlan BuildPolicyShardPlan(IReadOnlyList<string>? changedFiles)
    {
        if (changedFiles is null || changedFiles.Count == 0)
            return PolicyShardPlan.NotApplicable("no changed files");

        var normalizedFiles = changedFiles
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(NormalizePath)
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (normalizedFiles.Length == 0)
            return PolicyShardPlan.NotApplicable("no changed files");

        var summary = RepositoryChangeClassifier.Classify(normalizedFiles);
        var changedProjects = normalizedFiles
            .Select(TryMapPathToProject)
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var closure = BuildProjectDependencyClosure(changedProjects);
        var evidence = BuildPolicyShardEvidence(changedProjects, closure);
        var fullShardReason = FullShardReason(normalizedFiles, summary, changedProjects);
        return fullShardReason is not null
            ? PolicyShardPlan.Full($"{fullShardReason}; {evidence}", closure)
            : PolicyShardPlan.Scoped(evidence, closure);
    }

    private static string? FullShardReason(
        IReadOnlyList<string> changedFiles,
        RepositoryChangeSummary summary,
        IReadOnlyList<string> changedProjects)
    {
        if (FullShardOverrideEnabled())
            return "MCG_ACCEPTANCE_FULL_SHARDS=1";

        if (!ChangeScopedAcceptanceEnabled())
            return "MCG_ACCEPTANCE_CHANGE_SCOPED disabled";

        foreach (var path in changedFiles)
        {
            var fileName = Path.GetFileName(path);
            var extension = Path.GetExtension(path);
            if (extension.Equals(".csproj", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".props", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".targets", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".sln", StringComparison.OrdinalIgnoreCase) ||
                fileName.Equals("global.json", StringComparison.OrdinalIgnoreCase) ||
                fileName.Equals("AssemblyInfo.cs", StringComparison.OrdinalIgnoreCase))
            {
                return $"build-system file changed: {path}";
            }

            if (path.StartsWith("scripts/", StringComparison.OrdinalIgnoreCase))
                return $"script changed: {path}";

            if (path.Equals(
                    "src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/GoalAcceptanceVerifier.cs",
                    StringComparison.OrdinalIgnoreCase) ||
                path.Equals(
                    "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/GoalAcceptanceVerifierTests.cs",
                    StringComparison.OrdinalIgnoreCase))
            {
                return $"acceptance verifier code changed: {path}";
            }
        }

        if (!summary.IsDocsOnly && summary.HasBehaviorChanges && changedProjects.Count == 0)
            return "changed files did not map to a known project";

        return null;
    }

    private static bool FullShardOverrideEnabled()
    {
        var value = Environment.GetEnvironmentVariable("MCG_ACCEPTANCE_FULL_SHARDS");
        return value is not null &&
            (value.Equals("1", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("on", StringComparison.OrdinalIgnoreCase));
    }

    private static HashSet<string> BuildProjectDependencyClosure(IReadOnlyList<string> changedProjects)
    {
        var closure = new HashSet<string>(changedProjects, StringComparer.OrdinalIgnoreCase);
        var pending = new Queue<string>(changedProjects);
        while (pending.Count > 0)
        {
            var project = pending.Dequeue();
            if (!ReferencingProjectsByProject.TryGetValue(project, out var referencingProjects))
                continue;

            foreach (var referencingProject in referencingProjects)
            {
                if (closure.Add(referencingProject))
                    pending.Enqueue(referencingProject);
            }
        }

        return closure;
    }

    private static string BuildPolicyShardEvidence(
        IReadOnlyList<string> changedProjects,
        IReadOnlySet<string> closure)
    {
        var changed = changedProjects.Count == 0
            ? "(none)"
            : string.Join(", ", changedProjects.Select(ProjectLabel));
        var affected = closure.Count == 0
            ? "(none)"
            : string.Join(", ", closure.OrderBy(ProjectLabel, StringComparer.OrdinalIgnoreCase).Select(ProjectLabel));
        return $"changed projects: {changed}; dependency closure: {affected}";
    }

    private static string? TryMapPathToProject(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        var normalized = NormalizePath(path)!;
        if (normalized.StartsWith("src/Mcg.AgentOrchestrator.Core/", StringComparison.OrdinalIgnoreCase))
            return CoreProject;
        if (normalized.StartsWith("src/Mcg.AgentOrchestrator.Infrastructure/", StringComparison.OrdinalIgnoreCase))
            return InfrastructureProject;
        if (normalized.StartsWith("src/Mcg.AgentOrchestrator.App/", StringComparison.OrdinalIgnoreCase))
            return AppProject;
        if (normalized.StartsWith("tests/Mcg.AgentOrchestrator.Core.Tests/", StringComparison.OrdinalIgnoreCase))
            return CoreTestsProject;
        if (normalized.StartsWith("tests/Mcg.AgentOrchestrator.Infrastructure.Tests/", StringComparison.OrdinalIgnoreCase))
            return InfrastructureTestsProject;
        return null;
    }

    private static string ProjectLabel(string project) =>
        project.Equals(CoreProject, StringComparison.OrdinalIgnoreCase) ? "Core" :
        project.Equals(InfrastructureProject, StringComparison.OrdinalIgnoreCase) ? "Infrastructure" :
        project.Equals(AppProject, StringComparison.OrdinalIgnoreCase) ? "App" :
        project.Equals(CoreTestsProject, StringComparison.OrdinalIgnoreCase) ? "Core.Tests" :
        project.Equals(InfrastructureTestsProject, StringComparison.OrdinalIgnoreCase) ? "Infrastructure.Tests" :
        project;

    private static List<AcceptanceManifestCheck> ExpandBroadInfrastructureChecks(
        IReadOnlyList<AcceptanceManifestCheck> manifestChecks)
    {
        var effective = new List<AcceptanceManifestCheck>();
        foreach (var check in manifestChecks)
        {
            if (!IsBroadInfrastructureTestCheck(check))
            {
                effective.Add(check);
                continue;
            }

            effective.AddRange(ExpandBroadInfrastructureCheck(check));
        }

        return effective;
    }

    private static IEnumerable<AcceptanceManifestCheck> ExpandBroadInfrastructureCheck(AcceptanceManifestCheck check)
    {
        if (!IsBroadInfrastructureTestCheck(check))
        {
            yield return check;
            yield break;
        }

        foreach (var lane in InfrastructureTestLanes)
        {
            yield return new AcceptanceManifestCheck
            {
                Name = $"{check.Name}: {lane.Name}",
                Type = check.Type,
                Command = check.Command,
                Project = check.Project,
                Arguments = [.. check.Arguments, "--filter", lane.Filter],
                TimeoutMinutes = check.TimeoutMinutes
            };
        }
    }

    private static bool IsBroadInfrastructureTestCheck(AcceptanceManifestCheck check) =>
        check.Type.Equals("dotnet-test", StringComparison.OrdinalIgnoreCase) &&
        !string.IsNullOrWhiteSpace(check.Project) &&
        IsInfrastructureTestProject(check.Project) &&
        !check.Arguments.Any(argument => argument.Equals("--filter", StringComparison.OrdinalIgnoreCase));

    private static bool IsFocusedProjectDotnetCheck(AcceptanceManifestCheck check) =>
        check.Type.Equals("dotnet-test", StringComparison.OrdinalIgnoreCase) &&
        !string.IsNullOrWhiteSpace(check.Project) &&
        check.Project.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) &&
        check.Arguments.Any(argument => argument.Equals("--filter", StringComparison.OrdinalIgnoreCase));

    private static bool IsFullPolicyShardCheck(AcceptanceManifestCheck check) =>
        IsPolicyShardProjectCheck(check) &&
        !check.Arguments.Any(argument => argument.Equals("--filter", StringComparison.OrdinalIgnoreCase));

    private static bool IsPolicyShardProjectCheck(AcceptanceManifestCheck check) =>
        check.Type.Equals("dotnet-test", StringComparison.OrdinalIgnoreCase) &&
        !string.IsNullOrWhiteSpace(check.Project) &&
        (IsCoreTestProject(check.Project) || IsInfrastructureTestProject(check.Project));

    private static bool IsSkippedPolicyShardCheck(AcceptanceManifestCheck check, PolicyShardPlan policyShardPlan) =>
        policyShardPlan.Applies &&
        !policyShardPlan.ForceFull &&
        IsFullPolicyShardCheck(check) &&
        !policyShardPlan.IncludesProject(check.Project);

    private static bool IsRunnablePolicyShardCheck(AcceptanceManifestCheck check, PolicyShardPlan policyShardPlan) =>
        policyShardPlan.Applies &&
        !policyShardPlan.ForceFull &&
        IsPolicyShardProjectCheck(check) &&
        policyShardPlan.IncludesProject(check.Project);

    private static bool IsInfrastructureTestProject(string project) =>
        project.EndsWith(
            InfrastructureTestsProject,
            StringComparison.OrdinalIgnoreCase) ||
        project.EndsWith(
            "tests\\Mcg.AgentOrchestrator.Infrastructure.Tests\\Mcg.AgentOrchestrator.Infrastructure.Tests.csproj",
            StringComparison.OrdinalIgnoreCase);

    private static bool IsCoreTestProject(string project) =>
        project.EndsWith(
            CoreTestsProject,
            StringComparison.OrdinalIgnoreCase) ||
        project.EndsWith(
            "tests\\Mcg.AgentOrchestrator.Core.Tests\\Mcg.AgentOrchestrator.Core.Tests.csproj",
            StringComparison.OrdinalIgnoreCase);

    private static bool IsReplacedByFocusedProjectCheck(
        AcceptanceManifestCheck manifestCheck,
        IReadOnlyList<AcceptanceManifestCheck> focusedProjectChecks) =>
        manifestCheck.Type.Equals("dotnet-test", StringComparison.OrdinalIgnoreCase) &&
        !string.IsNullOrWhiteSpace(manifestCheck.Project) &&
        manifestCheck.Project.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) &&
        !manifestCheck.Arguments.Any(argument => argument.Equals("--filter", StringComparison.OrdinalIgnoreCase)) &&
        focusedProjectChecks.Any(focused =>
            string.Equals(NormalizePath(focused.Project), NormalizePath(manifestCheck.Project), StringComparison.OrdinalIgnoreCase));

    private static string ManifestCheckKey(AcceptanceManifestCheck check) =>
        $"{check.Type}:{check.Command}:{NormalizePath(check.Project)}:{string.Join('\u001f', check.Arguments)}";

    private static AcceptanceManifestCheck? PolicyCheckToManifestCheck(VerificationPolicyCheck check)
    {
        if (check.Kind.Equals("dotnet-test", StringComparison.OrdinalIgnoreCase))
        {
            var parts = SplitCommandLine(check.CommandLine);
            if (parts.Length < 2 ||
                !parts[0].Equals("dotnet", StringComparison.OrdinalIgnoreCase) ||
                !parts[1].Equals("test", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return DotnetCommandToManifestCheck(check.Name, parts);
        }

        if (check.Kind.Equals("browser-smoke", StringComparison.OrdinalIgnoreCase))
        {
            var parts = SplitCommandLine(check.CommandLine);
            var script = parts.Length == 0 ? @".\scripts\Run-DashboardBrowserScript.ps1" : parts[0];
            string[] scriptArguments = parts.Length > 1
                ? parts[1..]
                : [@".\scripts\dashboard-smoke.js"];
            return new AcceptanceManifestCheck
            {
                Name = check.Name,
                Type = "browser-smoke",
                Command = "powershell",
                Arguments = ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script, .. scriptArguments]
            };
        }

        return null;
    }

    private static AcceptanceManifestCheck PolicyCheckToManifestCheck(RepositoryTestImpactCheck check)
    {
        // Command format: ["dotnet", "test", <optional project>, ...args]
        return DotnetCommandToManifestCheck(check.Name, [.. check.Command]);
    }

    private static AcceptanceManifestCheck DotnetCommandToManifestCheck(string name, string[] command)
    {
        var remaining = command.Skip(2).ToArray();
        var project = remaining.Length > 0 && !remaining[0].StartsWith("-", StringComparison.Ordinal)
            ? remaining[0]
            : null;
        var arguments = project is null ? remaining : remaining.Skip(1).ToArray();
        return new AcceptanceManifestCheck
        {
            Name = name,
            Type = "dotnet-test",
            Project = project,
            Arguments = arguments
        };
    }

    private static string[] SplitCommandLine(string commandLine) =>
        commandLine.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static void AddCoveredBroadInfrastructureResults(
        List<AcceptanceCheckResult> checks,
        IReadOnlyList<AcceptanceManifestCheck> policyEffectiveChecks,
        IReadOnlyList<string>? changedFiles)
    {
        if (changedFiles is null || changedFiles.Count == 0)
            return;

        foreach (var broadCheck in policyEffectiveChecks.Where(IsBroadInfrastructureTestCheck))
        {
            if (checks.Any(result => result.Name.Equals(broadCheck.Name, StringComparison.OrdinalIgnoreCase)))
                continue;

            var shardResults = ExpandBroadInfrastructureCheck(broadCheck)
                .Select(shard => checks.FirstOrDefault(result =>
                    result.Name.Equals(shard.Name, StringComparison.OrdinalIgnoreCase)))
                .Where(result => result is not null)
                .Select(result => result!)
                .ToArray();
            if (shardResults.Length == 0)
                continue;

            var failedShard = shardResults.FirstOrDefault(result => !result.Passed);
            if (failedShard is not null)
            {
                checks.Add(new AcceptanceCheckResult(
                    broadCheck.Name,
                    false,
                    failedShard.ExitCode,
                    failedShard.OutputTail,
                    failedShard.ArtifactsPath,
                    failedShard.BrokerName,
                    failedShard.LeaseId,
                    failedShard.DurationMilliseconds,
                    failedShard.LockRemediationApplied,
                    $"covered by failed partition: {failedShard.Name}"));
                continue;
            }

            if (shardResults.Length != InfrastructureTestLanes.Length)
                continue;

            var lastShard = shardResults[^1];
            checks.Add(new AcceptanceCheckResult(
                broadCheck.Name,
                true,
                0,
                null,
                lastShard.ArtifactsPath,
                lastShard.BrokerName,
                lastShard.LeaseId,
                shardResults.Sum(result => result.DurationMilliseconds ?? 0),
                shardResults.Any(result => result.LockRemediationApplied),
                $"covered by {shardResults.Length} partitioned checks"));
        }
    }

    private static void AddCoveredPolicyAliasResults(
        List<AcceptanceCheckResult> checks,
        IReadOnlyList<AcceptanceManifestCheck> policyRequiredChecks,
        IReadOnlyList<AcceptanceManifestCheck> effectiveChecks)
    {
        foreach (var policyCheck in policyRequiredChecks)
        {
            if (IsBroadInfrastructureTestCheck(policyCheck) ||
                checks.Any(result => result.Name.Equals(policyCheck.Name, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var policyKey = ManifestCheckKey(policyCheck);
            var coveringCheck = effectiveChecks.FirstOrDefault(check =>
                ManifestCheckKey(check).Equals(policyKey, StringComparison.OrdinalIgnoreCase) &&
                !check.Name.Equals(policyCheck.Name, StringComparison.OrdinalIgnoreCase));
            if (coveringCheck is null)
                continue;

            var coveringResult = checks.FirstOrDefault(result =>
                result.Name.Equals(coveringCheck.Name, StringComparison.OrdinalIgnoreCase));
            if (coveringResult is null)
                continue;

            checks.Add(new AcceptanceCheckResult(
                policyCheck.Name,
                coveringResult.Passed,
                coveringResult.ExitCode,
                coveringResult.Passed ? null : coveringResult.OutputTail,
                coveringResult.ArtifactsPath,
                coveringResult.BrokerName,
                coveringResult.LeaseId,
                coveringResult.DurationMilliseconds,
                coveringResult.LockRemediationApplied,
                $"covered by: {coveringCheck.Name}"));
        }
    }

    private static void AddPolicyShardReceiptResults(
        List<AcceptanceCheckResult> checks,
        IReadOnlyList<AcceptanceManifestCheck> manifestChecks,
        IReadOnlyList<AcceptanceManifestCheck> effectiveChecks,
        PolicyShardPlan policyShardPlan)
    {
        if (!policyShardPlan.Applies || policyShardPlan.ForceFull)
            return;

        foreach (var shard in manifestChecks.Where(IsFullPolicyShardCheck))
        {
            if (checks.Any(result => result.Name.Equals(shard.Name, StringComparison.OrdinalIgnoreCase)))
                continue;

            if (!policyShardPlan.IncludesProject(shard.Project))
            {
                checks.Add(new AcceptanceCheckResult(
                    shard.Name,
                    true,
                    null,
                    null,
                    ResultSummary:
                    $"skipped: no changed file in dependency closure; shard project: {ProjectLabel(NormalizePath(shard.Project)!)}; {policyShardPlan.Evidence}"));
                continue;
            }

            var coveringCheck = effectiveChecks.FirstOrDefault(check =>
                IsPolicyShardProjectCheck(check) &&
                string.Equals(NormalizePath(check.Project), NormalizePath(shard.Project), StringComparison.OrdinalIgnoreCase));
            if (coveringCheck is null)
                continue;

            var coveringResult = checks.FirstOrDefault(result =>
                result.Name.Equals(coveringCheck.Name, StringComparison.OrdinalIgnoreCase));
            if (coveringResult is null)
                continue;

            checks.Add(new AcceptanceCheckResult(
                shard.Name,
                coveringResult.Passed,
                coveringResult.ExitCode,
                coveringResult.Passed ? null : coveringResult.OutputTail,
                coveringResult.ArtifactsPath,
                coveringResult.BrokerName,
                coveringResult.LeaseId,
                coveringResult.DurationMilliseconds,
                coveringResult.LockRemediationApplied,
                $"covered by: {coveringCheck.Name}; changed file in dependency closure; {policyShardPlan.Evidence}"));
        }
    }

    private static bool IsProjectInSolution(string projectPath, string? solutionProject, string? slnContent)
    {
        if (string.IsNullOrWhiteSpace(solutionProject))
            return true;

        if (slnContent is null)
            return true;

        return slnContent.Contains(Path.GetFileName(projectPath), StringComparison.OrdinalIgnoreCase);
    }

    private static bool ProposalValidationApplies(string worktreePath, IReadOnlyList<string>? changedFiles) =>
        changedFiles is { Count: > 0 }
            ? changedFiles.Any(StateEffectProposalParser.IsProposalPath)
            : Directory.Exists(Path.Combine(worktreePath, ".orchestrator-proposals"));

    private static AcceptanceCheckResult RunStateEffectProposalSchemaCheck(
        string worktreePath,
        IReadOnlyList<string>? changedFiles)
    {
        var result = StateEffectProposalParser.ValidateDirectory(worktreePath, changedFiles);
        return new AcceptanceCheckResult(
            "state-effect proposal schema",
            result.Passed,
            result.Passed ? 0 : 1,
            result.Passed ? null : result.Summary,
            ResultSummary: result.Summary);
    }

    private static AcceptanceManifestCheck[] LoadAdvisoryChecks(string worktreePath)
    {
        var path = System.IO.Path.Combine(worktreePath, ".orchestrator", "goal-acceptance-criteria.json");
        if (!File.Exists(path))
            return [];

        try
        {
            var criteria = JsonSerializer.Deserialize<AcceptanceCriterion[]>(
                File.ReadAllText(path),
                CriteriaJsonOptions) ?? [];
            return criteria
                .Where(c => !string.IsNullOrWhiteSpace(c.Type))
                .Select(c => CriterionToManifestCheck(c))
                .ToArray();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static AcceptanceManifestCheck CriterionToManifestCheck(AcceptanceCriterion criterion)
    {
        // For command-exit, the stored Command is the full command line (e.g. "dotnet build Foo.sln -c Release").
        // Split it into executable + arguments for process launch.
        if (criterion.Type.Equals("command-exit", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(criterion.Command))
        {
            var parts = criterion.Command.Split([' '], StringSplitOptions.RemoveEmptyEntries);
            return new AcceptanceManifestCheck
            {
                Name = criterion.Name,
                Type = "command-exit",
                Command = parts[0],
                Arguments = parts.Length > 1 ? parts[1..] : [],
                Advisory = true
            };
        }

        return new AcceptanceManifestCheck
        {
            Name = criterion.Name,
            Type = criterion.Type,
            Command = criterion.Command,
            Pattern = criterion.Pattern,
            FilePath = criterion.Path,
            Advisory = true
        };
    }

    private async Task<(AcceptanceCheckResult Result, bool Retried)> RunCheckAsync(
        AcceptanceManifestCheck check,
        string worktreePath,
        GoalId? goalId,
        int? stableSlotIndex,
        DotnetBuildEnvironmentLease? stableSlotLease,
        CancellationToken cancellationToken)
    {
        if (check.Type.Equals("no-op", StringComparison.OrdinalIgnoreCase))
        {
            return (new AcceptanceCheckResult(check.Name, true, null, null, Advisory: check.Advisory), false);
        }

        if (check.Type.Equals("grep-absent", StringComparison.OrdinalIgnoreCase))
            return (await RunGrepCheckAsync(check, worktreePath, expectPresent: false, cancellationToken).ConfigureAwait(false), false);

        if (check.Type.Equals("grep-present", StringComparison.OrdinalIgnoreCase))
            return (await RunGrepCheckAsync(check, worktreePath, expectPresent: true, cancellationToken).ConfigureAwait(false), false);

        if (check.Type.Equals("file-exists", StringComparison.OrdinalIgnoreCase))
            return (RunFileExistsCheck(check, worktreePath), false);

        if (check.Type.Equals("command-exit", StringComparison.OrdinalIgnoreCase))
            return await RunCommandCheckAsync(check, worktreePath, goalId, stableSlotIndex, stableSlotLease, cancellationToken).ConfigureAwait(false);

        return check.Type.Equals("dotnet-test", StringComparison.OrdinalIgnoreCase)
            ? await RunDotnetTestCheckAsync(check, worktreePath, goalId, stableSlotIndex, stableSlotLease, cancellationToken).ConfigureAwait(false)
            : await RunCommandCheckAsync(check, worktreePath, goalId, stableSlotIndex, stableSlotLease, cancellationToken).ConfigureAwait(false);
    }

    private async Task<AcceptanceCheckResult> RunGrepCheckAsync(
        AcceptanceManifestCheck check,
        string worktreePath,
        bool expectPresent,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(check.Pattern))
        {
            return new AcceptanceCheckResult(
                check.Name, false, 1,
                "grep check has no pattern configured",
                Advisory: check.Advisory);
        }

        var result = await _runner(
            ["git", "grep", "-q", "--", check.Pattern],
            worktreePath,
            AcceptanceCheckTimeouts.Resolve(check.TimeoutMinutes),
            cancellationToken).ConfigureAwait(false);

        // git grep exit 0 = pattern found, exit 1 = not found
        var patternFound = result.ExitCode == 0;
        var passed = expectPresent ? patternFound : !patternFound;
        var summary = expectPresent
            ? (patternFound ? "pattern found" : "pattern not found")
            : (patternFound ? "pattern still present" : "pattern absent");

        return new AcceptanceCheckResult(
            check.Name,
            passed,
            passed ? 0 : 1,
            passed ? null : $"Advisory check failed: {summary} for pattern '{check.Pattern}'",
            ResultSummary: summary,
            Advisory: check.Advisory);
    }

    private AcceptanceCheckResult RunFileExistsCheck(
        AcceptanceManifestCheck check,
        string worktreePath)
    {
        if (string.IsNullOrWhiteSpace(check.FilePath))
        {
            return new AcceptanceCheckResult(
                check.Name, false, 1,
                "file-exists check has no path configured",
                Advisory: check.Advisory);
        }

        var fullPath = System.IO.Path.Combine(worktreePath, check.FilePath);
        var exists = File.Exists(fullPath);
        return new AcceptanceCheckResult(
            check.Name,
            exists,
            exists ? 0 : 1,
            exists ? null : $"Advisory check failed: file not found: {check.FilePath}",
            ResultSummary: exists ? "file exists" : "file not found",
            Advisory: check.Advisory);
    }

    private async Task<(AcceptanceCheckResult Result, bool Retried)> RunCommandCheckAsync(
        AcceptanceManifestCheck check,
        string worktreePath,
        GoalId? goalId,
        int? stableSlotIndex,
        DotnetBuildEnvironmentLease? stableSlotLease,
        CancellationToken cancellationToken)
    {
        var arguments = BuildCommandArguments(check);
        if (IsDotnetCommand(arguments))
        {
            return await RunManagedDotnetCheckAsync(
                check,
                arguments,
                worktreePath,
                goalId,
                stableSlotIndex,
                stableSlotLease,
                $"acceptance-{Slug(check.Name)}",
                cancellationToken).ConfigureAwait(false);
        }

        var result = await _runner(arguments, worktreePath, AcceptanceCheckTimeouts.Resolve(check.TimeoutMinutes), cancellationToken).ConfigureAwait(false);
        if (result.TimedOut)
        {
            return (new AcceptanceCheckResult(
                BuildTimeoutFailureName(check, result),
                false,
                result.ExitCode,
                BuildTimeoutOutput(result),
                ResultSummary: BuildTimeoutSummary(result),
                Advisory: check.Advisory), false);
        }

        return (new AcceptanceCheckResult(
            check.Name,
            result.ExitCode == 0,
            result.ExitCode,
            result.ExitCode == 0 ? null : TailOutput(result.Output),
            Advisory: check.Advisory), false);
    }

    private async Task<(AcceptanceCheckResult Result, bool Retried)> RunDotnetTestCheckAsync(
        AcceptanceManifestCheck check,
        string worktreePath,
        GoalId? goalId,
        int? stableSlotIndex,
        DotnetBuildEnvironmentLease? stableSlotLease,
        CancellationToken cancellationToken)
    {
        return await RunManagedDotnetCheckAsync(
            check,
            BuildDotnetTestArguments(check),
            worktreePath,
            goalId,
            stableSlotIndex,
            stableSlotLease,
            $"acceptance-{Slug(check.Name)}",
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<(AcceptanceCheckResult Result, bool Retried)> RunManagedDotnetCheckAsync(
        AcceptanceManifestCheck check,
        string[] arguments,
        string worktreePath,
        GoalId? goalId,
        int? stableSlotIndex,
        DotnetBuildEnvironmentLease? stableSlotLease,
        string attemptName,
        CancellationToken cancellationToken)
    {
        var elapsed = Stopwatch.StartNew();
        var environment = stableSlotLease?.Environment ?? (stableSlotIndex.HasValue
            ? DotnetBuildEnvironmentManager.CreateStableSlotAttempt(stableSlotIndex.Value)
            : DotnetBuildEnvironmentManager.CreateAttempt(goalId, attemptName));
        FileStream? leaseLock = null;
        try
        {
            leaseLock = stableSlotLease is null
                ? DotnetBuildEnvironmentManager.AcquireLeaseExecutionLock(environment, cancellationToken)
                : null;

            var result = await _runner(
                WithBuildEnvironmentArguments(arguments, environment),
                worktreePath,
                AcceptanceCheckTimeouts.Resolve(check.TimeoutMinutes),
                cancellationToken).ConfigureAwait(false);

            var transientCompilerLockRetried = false;
            if (!result.TimedOut &&
                result.ExitCode != 0 &&
                IsTransientCompilerLockFailure(result.Output))
            {
                await _runner(
                    ["dotnet", "build-server", "shutdown"],
                    worktreePath,
                    AcceptanceCheckTimeouts.DefaultTimeout,
                    cancellationToken).ConfigureAwait(false);
                if (stableSlotLease is null)
                {
                    leaseLock?.Dispose();
                    leaseLock = null;
                    environment = stableSlotIndex.HasValue
                        ? DotnetBuildEnvironmentManager.CreateStableSlotAttempt(stableSlotIndex.Value)
                        : DotnetBuildEnvironmentManager.CreateAttempt(goalId, $"{attemptName}-retry");
                    leaseLock = DotnetBuildEnvironmentManager.AcquireLeaseExecutionLock(environment, cancellationToken);
                }

                result = await _runner(
                    WithBuildEnvironmentArguments(arguments, environment),
                    worktreePath,
                    AcceptanceCheckTimeouts.Resolve(check.TimeoutMinutes),
                    cancellationToken).ConfigureAwait(false);
                transientCompilerLockRetried = true;
            }

            elapsed.Stop();
            // A testhost can exit non-zero on SHUTDOWN ("host process exited unexpectedly") even after every
            // test passed. Honor the run's own Passed!/Failed:0 summary so a benign shutdown abort does not
            // block a green goal, while never masking a build/compile failure and still surfacing the tail.
            var reportedAllPassed = !result.TimedOut && result.ExitCode != 0 && TestRunReportsAllPassed(result.Output);
            var passed = !result.TimedOut && (result.ExitCode == 0 || reportedAllPassed);
            return (new AcceptanceCheckResult(
                result.TimedOut ? BuildTimeoutFailureName(check, result) : check.Name,
                passed,
                result.ExitCode,
                result.TimedOut
                    ? BuildTimeoutOutput(result)
                    : passed && result.ExitCode == 0 ? null : TailOutput(result.Output),
                environment.ArtifactsPath,
                "goal-acceptance-verifier",
                environment.LeaseId,
                (long)elapsed.Elapsed.TotalMilliseconds,
                transientCompilerLockRetried,
                BuildManagedDotnetResultSummary(result, transientCompilerLockRetried)), transientCompilerLockRetried);
        }
        finally
        {
            leaseLock?.Dispose();
        }
    }

    // A dotnet test run whose own summary banner is "Passed!" (zero failed) but which then exits non-zero
    // is a testhost shutdown abort, not a test failure. Treat it as passed so a benign abort does not block
    // a green goal; a build/compile failure ("Build FAILED" / "error CS...") is a real failure, not this.
    private static bool TestRunReportsAllPassed(string output)
    {
        if (output.Contains("Build FAILED", StringComparison.Ordinal) ||
            output.Contains("error CS", StringComparison.Ordinal))
        {
            return false;
        }

        return output.Contains("Passed!", StringComparison.Ordinal) &&
            !output.Contains("Failed!", StringComparison.Ordinal);
    }

    private static bool IsTransientCompilerLockFailure(string output) =>
        (output.Contains("error CS2012", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("MSB3491", StringComparison.OrdinalIgnoreCase)) &&
        output.Contains("being used by another process", StringComparison.OrdinalIgnoreCase);

    private static string? BuildManagedDotnetResultSummary(CommandResult result, bool transientCompilerLockRetried)
    {
        var summary = AppendResourceReceipt(
            result.TimedOut ? BuildTimeoutSummary(result) : ExtractResultSummary(result.Output),
            result.ResourceAccounting);
        if (!transientCompilerLockRetried)
        {
            return summary;
        }

        const string remediation = "transient compiler lock detected; build server restarted; check retried";
        return string.IsNullOrWhiteSpace(summary)
            ? remediation
            : $"{remediation}; {summary}";
    }

    private static string? AppendResourceReceipt(string? summary, TaskProcessResourceAccounting? accounting)
    {
        if (accounting is null)
        {
            return summary;
        }

        var receipt = $"RESOURCE phase=gate cpu_ms={accounting.CpuMilliseconds} peak_mem_bytes={accounting.PeakMemoryBytes} io_bytes={accounting.IoBytes}";
        return string.IsNullOrWhiteSpace(summary)
            ? receipt
            : $"{summary}; {receipt}";
    }

    // A testhost that crashes MID-run ("host process exited unexpectedly" / "Test Run Aborted") with no
    // completed all-passed banner and no real test failure is an environmental abort, not a verdict. A
    // build/compile failure, or a completed run WITH real test failures, is NOT this (it is a genuine red).
    internal static bool IsTransientTesthostAbort(string output)
    {
        if (output.Contains("Build FAILED", StringComparison.Ordinal) ||
            output.Contains("error CS", StringComparison.Ordinal) ||
            TestRunReportsRealFailure(output))
        {
            return false;
        }

        return output.Contains("Test Run Aborted", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("host process exited unexpectedly", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("active Test Run was aborted", StringComparison.OrdinalIgnoreCase);
    }

    // True when the run's own summary banner reports >=1 actual test failure (a completed run with real
    // failures), e.g. "Failed:     1, Passed:  1012". Distinguishes a genuine red from a transient abort.
    private static bool TestRunReportsRealFailure(string output) =>
        System.Text.RegularExpressions.Regex.IsMatch(output, @"Failed:\s*[1-9]\d*");

    private async Task<AcceptanceCheckResult> RunForbiddenChangedPathsCheckAsync(
        IReadOnlyList<string> globs,
        string worktreePath,
        CancellationToken cancellationToken)
    {
        var result = await _runner(
            ["git", "diff", "--name-only", "main...HEAD"],
            worktreePath,
            AcceptanceCheckTimeouts.DefaultTimeout,
            cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            return new AcceptanceCheckResult("forbidden changed paths", false, result.ExitCode, TailOutput(result.Output));
        }

        var changedPaths = result.Output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var forbidden = changedPaths
            .Where(path => globs.Any(glob => GlobMatches(glob, path)))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return forbidden.Length == 0
            ? new AcceptanceCheckResult("forbidden changed paths", true, 0, null)
            : new AcceptanceCheckResult("forbidden changed paths", false, 1, string.Join(Environment.NewLine, forbidden));
    }

    private async Task<AcceptanceCheckResult> RunTestTamperCheckAsync(
        string[] testFiles,
        string worktreePath,
        CancellationToken cancellationToken)
    {
        const string CheckName = "test tamper guard";

        var diffArgs = DiffBaseArgs.Concat(testFiles).ToArray();

        var result = await _runner(
            diffArgs,
            worktreePath,
            AcceptanceCheckTimeouts.DefaultTimeout,
            cancellationToken).ConfigureAwait(false);

        if (result.ExitCode != 0)
            return new AcceptanceCheckResult(CheckName, true, 0, null, Advisory: true, ResultSummary: "diff unavailable");

        var signals = AnalyzeTestFileDiff(result.Output);

        if (signals.Count == 0)
            return new AcceptanceCheckResult(CheckName, true, 0, null, Advisory: true, ResultSummary: "no test degradation detected");

        return new AcceptanceCheckResult(
            CheckName, false, 1,
            string.Join(Environment.NewLine, signals),
            Advisory: true,
            ResultSummary: $"{signals.Count} test degradation signal(s)");
    }

    private static bool IsTestFile(string path) =>
        path.Contains("Tests", StringComparison.OrdinalIgnoreCase);

    private static List<string> AnalyzeTestFileDiff(string diff)
    {
        var signals = new List<string>();
        string? currentFile = null;
        string? pendingFile = null;
        int assertRemoved = 0, assertAdded = 0;
        int testAttrRemoved = 0, testAttrAdded = 0;
        var tautologies = new List<string>();

        void FlushFile()
        {
            if (currentFile is null) return;
            var fileSignals = new List<string>();

            var netAssert = assertRemoved - assertAdded;
            if (netAssert > 0)
                fileSignals.Add($"net -{netAssert} assertion(s) removed");

            var netTestAttr = testAttrRemoved - testAttrAdded;
            if (netTestAttr > 0)
                fileSignals.Add($"{netTestAttr} test method(s) removed");

            foreach (var t in tautologies)
                fileSignals.Add($"tautology assertion added: {t}");

            if (fileSignals.Count > 0)
                signals.Add($"{currentFile}: {string.Join("; ", fileSignals)}");
        }

        void StartFile(string filePath)
        {
            FlushFile();
            currentFile = filePath;
            assertRemoved = assertAdded = testAttrRemoved = testAttrAdded = 0;
            tautologies.Clear();
        }

        foreach (var rawLine in diff.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');

            if (line.StartsWith("--- a/", StringComparison.Ordinal))
            {
                pendingFile = line[6..];
            }
            else if (line.StartsWith("+++ b/", StringComparison.Ordinal))
            {
                StartFile(line[6..]);
                pendingFile = null;
            }
            else if (line.StartsWith("+++ /dev/null", StringComparison.Ordinal) && pendingFile is not null)
            {
                StartFile(pendingFile);
                pendingFile = null;
            }
            else if (line.Length > 1 && line[0] is '-' or '+' &&
                     !line.StartsWith("--- ", StringComparison.Ordinal) &&
                     !line.StartsWith("+++ ", StringComparison.Ordinal))
            {
                var content = line[1..];
                var trimmed = content.TrimStart();

                if (line[0] == '-')
                {
                    if (trimmed.StartsWith("Assert.", StringComparison.Ordinal))
                        assertRemoved++;
                    if (TestAttrPattern.IsMatch(trimmed))
                        testAttrRemoved++;
                }
                else
                {
                    if (trimmed.StartsWith("Assert.", StringComparison.Ordinal))
                        assertAdded++;
                    if (TestAttrPattern.IsMatch(trimmed))
                        testAttrAdded++;
                    if (TautologyPattern.IsMatch(content))
                        tautologies.Add(trimmed.Length > 80 ? trimmed[..80] + "..." : trimmed);
                }
            }
        }

        FlushFile();
        return signals;
    }

    private static string[] BuildDotnetTestArguments(AcceptanceManifestCheck check)
    {
        var args = new List<string> { "dotnet", "test" };
        if (!string.IsNullOrWhiteSpace(check.Project))
        {
            args.Add(check.Project);
        }

        var explicitFilter = ExtractFilterArguments(check.Arguments, args);

        // Exclude host-integration tests that spawn a real Kestrel dashboard server (binds a port,
        // needs an interactive firewall allow) — they hang in the unattended, relocated gate. Match
        // both by class name (works on a worktree built before the trait existed) and by the
        // [Trait("Category","HostIntegration")] tag (covers any future such tests). They run in a
        // dedicated lane instead.
        if (string.IsNullOrWhiteSpace(explicitFilter) && NeedsUnattendedHostIntegrationExclusion(check))
        {
            args.Add("--filter");
            args.Add("FullyQualifiedName!~DashboardHostTests&Category!=HostIntegration");
        }

        // Fail a hung test fast and by name before the whole check budget is exhausted. A test that
        // spawns a process which blocks (e.g. on a firewall prompt) and then WaitForExit()s on it
        // can otherwise stall the whole acceptance until the configured command timeout. The
        // inactivity timeout is per-test and distinct from the full check budget.
        args.Add("--blame-hang-timeout");
        args.Add("120s");
        args.Add("--blame-hang-dump-type");
        args.Add("none");
        return [.. args];
    }

    private static bool NeedsUnattendedHostIntegrationExclusion(AcceptanceManifestCheck check) =>
        string.IsNullOrWhiteSpace(check.Project) ||
        check.Project.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) ||
        IsInfrastructureTestProject(check.Project);

    private static string? ExtractFilterArguments(IReadOnlyList<string> sourceArguments, List<string> destinationArguments)
    {
        string? filter = null;
        for (var index = 0; index < sourceArguments.Count; index++)
        {
            var argument = sourceArguments[index];
            if (argument.Equals("--filter", StringComparison.OrdinalIgnoreCase))
            {
                if (index + 1 < sourceArguments.Count)
                {
                    filter = sourceArguments[index + 1];
                    index++;
                }

                continue;
            }

            destinationArguments.Add(argument);
        }

        if (!string.IsNullOrWhiteSpace(filter))
        {
            destinationArguments.Add("--filter");
            destinationArguments.Add(filter);
        }

        return filter;
    }

    private static string[] WithBuildEnvironmentArguments(string[] arguments, DotnetBuildEnvironment environment)
    {
        return [.. arguments, .. environment.Arguments];
    }

    private static bool IsDotnetCommand(string[] arguments)
    {
        return arguments.Length > 0 && arguments[0].Equals("dotnet", StringComparison.OrdinalIgnoreCase);
    }

    private static string[] BuildCommandArguments(AcceptanceManifestCheck check)
    {
        if (string.IsNullOrWhiteSpace(check.Command))
        {
            throw new InvalidOperationException($"Acceptance check '{check.Name}' is missing command.");
        }

        return [check.Command, .. check.Arguments];
    }

    private static string Slug(string value)
    {
        var slug = Regex.Replace(value.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
        return string.IsNullOrWhiteSpace(slug) ? "check" : slug;
    }

    private static bool GlobMatches(string glob, string path)
    {
        var normalizedGlob = glob.Replace('\\', '/').TrimStart('/');
        var normalizedPath = path.Replace('\\', '/').TrimStart('/');
        var pattern = "^" + Regex.Escape(normalizedGlob)
            .Replace("\\*\\*", ".*", StringComparison.Ordinal)
            .Replace("\\*", "[^/]*", StringComparison.Ordinal) + "$";
        return Regex.IsMatch(normalizedPath, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static string? ExtractResultSummary(string output)
    {
        return output
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault(line =>
                line.Contains("Failed:", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("Passed:", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("Total:", StringComparison.OrdinalIgnoreCase));
    }

    private static string? TailOutput(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return null;
        }

        const int maxChars = 2000;
        const int maxLines = 40;

        var trimmed = output.Trim();
        var lines = trimmed.Split('\n');
        var tail = lines.Length <= maxLines
            ? trimmed
            : string.Join('\n', lines[^maxLines..]);

        return tail.Length <= maxChars ? tail : tail[^maxChars..];
    }

    private static string BuildTimeoutSummary(CommandResult result) =>
        $"elapsed={FormatTimeout(result.Elapsed ?? result.Timeout ?? AcceptanceCheckTimeouts.DefaultTimeout)} budget={FormatTimeout(result.Timeout ?? AcceptanceCheckTimeouts.DefaultTimeout)}";

    private static string BuildTimeoutFailureName(AcceptanceManifestCheck check, CommandResult result) =>
        $"acceptance-check-timeout: {Slug(check.Name)} {BuildTimeoutSummary(result)}";

    private static string BuildTimeoutOutput(CommandResult result)
    {
        var details = new List<string>
        {
            $"Verification command timed out after {BuildTimeoutSummary(result)}.",
        };

        if (!string.IsNullOrWhiteSpace(result.CommandLine))
            details.Add($"Command: {result.CommandLine}");
        if (!string.IsNullOrWhiteSpace(result.StdoutPath))
            details.Add($"stdout: {result.StdoutPath}");
        if (!string.IsNullOrWhiteSpace(result.StderrPath))
            details.Add($"stderr: {result.StderrPath}");

        var tail = TailOutput(result.Output);
        if (!string.IsNullOrWhiteSpace(tail))
        {
            details.Add("Last output:");
            details.Add(tail);
        }

        return string.Join(Environment.NewLine, details);
    }

    private static string FormatTimeout(TimeSpan timeout) =>
        timeout.TotalSeconds >= 60
            ? $"{timeout.TotalMinutes:0.#}m"
            : $"{timeout.TotalSeconds:0.#}s";

    private static async Task<CommandResult> RunProcessAsync(
        string[] arguments,
        string workingDirectory,
        TimeSpan commandTimeout,
        CancellationToken cancellationToken)
    {
        // Capture output to FILES via the platform shell, not pipes. A test or build can spawn a
        // grandchild that inherits the child's stdout/stderr handle and outlives it; with a
        // redirected PIPE the test runner never reaches EOF while that grandchild holds the write
        // end, so `dotnet test` never exits and the whole command rides the configured timeout to a
        // "A task was canceled". A plain `dotnet test > out 2> err` exits cleanly in that same
        // scenario, so we mirror it: every process exits regardless of a lingering grandchild and
        // we read the files afterward with a shared, delete-tolerant handle.
        var stdoutPath = Path.Combine(Path.GetTempPath(), $"mcg-acc-{Guid.NewGuid():N}.out");
        var stderrPath = Path.Combine(Path.GetTempPath(), $"mcg-acc-{Guid.NewGuid():N}.err");

        var commandLine = string.Join(' ', arguments.Select(QuoteForDisplay));
        var timedOut = false;
        var elapsed = Stopwatch.StartNew();
        var startInfo = new ProcessStartInfo
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory
        };

        if (OperatingSystem.IsWindows())
        {
            startInfo.FileName = "cmd.exe";
            // cmd /c strips one surrounding quote pair, so wrap the whole redirected command once.
            startInfo.Arguments = $"/c \"{BuildRedirectedCommand(arguments, stdoutPath, stderrPath, QuoteForCmd)}\"";
        }
        else
        {
            startInfo.FileName = "/bin/sh";
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add(BuildRedirectedCommand(arguments, stdoutPath, stderrPath, QuoteForPosix));
        }

        startInfo.EnvironmentVariables["MCG_ORCHESTRATOR_REPOSITORY_ROOT"] = workingDirectory;

        // The acceptance suite verifies the CODE and must run hermetically — NOT under the operator's
        // live worker-dispatch runtime config. MCG_WORKER_SANDBOX (and friends) control how real
        // workers are launched (low integrity); several tests read WorkerSandboxOptions.FromEnvironment(),
        // so when the operator runs `conduct` with MCG_WORKER_SANDBOX=1 that var is inherited by this
        // child process and flips those tests' expected sandbox mode — failing acceptance INSIDE the
        // watch while the same suite passes when `acceptance` is run standalone (without the var). Strip
        // the worker-dispatch vars so the suite always runs against the default configuration.
        startInfo.EnvironmentVariables.Remove(WorkerSandboxOptions.EnabledVariable);
        startInfo.EnvironmentVariables.Remove(WorkerSandboxOptions.AccountVariable);
        startInfo.EnvironmentVariables.Remove(WorkerSandboxOptions.CredentialTargetVariable);
        startInfo.EnvironmentVariables.Remove(WorkerSandboxOptions.DispatchWorkerVariable);

        int? startedProcessId = null;
        try
        {
            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException($"Failed to start process: {arguments[0]}");
            startedProcessId = process.Id;
            WorkerProcessJobs.TryRegister(process, $"acceptance:{workingDirectory}");

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(commandTimeout);

            try
            {
                await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { WorkerProcessJobs.TryKillOrFallback(process.Id); } catch { /* best effort */ }
                try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
                if (cancellationToken.IsCancellationRequested)
                    throw;

                timedOut = true;
            }

            var stdout = await ReadFileWithRetryAsync(stdoutPath).ConfigureAwait(false);
            var stderr = await ReadFileWithRetryAsync(stderrPath).ConfigureAwait(false);
            elapsed.Stop();
            var exitCode = timedOut ? -1 : process.ExitCode;
            WorkerProcessJobs.Release(process.Id, out var accounting);
            startedProcessId = null;
            return new CommandResult(
                exitCode,
                (stdout + stderr).Trim(),
                timedOut,
                commandLine,
                stdoutPath,
                stderrPath,
                commandTimeout,
                elapsed.Elapsed,
                accounting is null
                    ? null
                    : new TaskProcessResourceAccounting(
                        accounting.CpuMilliseconds,
                        accounting.PeakMemoryBytes,
                        accounting.IoBytes));
        }
        finally
        {
            if (startedProcessId is { } processId)
            {
                WorkerProcessJobs.Release(processId);
            }

            if (!timedOut)
            {
                TryDeleteFile(stdoutPath);
                TryDeleteFile(stderrPath);
            }
        }
    }

    private static string QuoteForDisplay(string value) =>
        value.Contains(' ', StringComparison.Ordinal) || value.Contains('"', StringComparison.Ordinal)
            ? $"\"{value.Replace("\"", "\\\"", StringComparison.Ordinal)}\""
            : value;

    private static string BuildRedirectedCommand(
        string[] arguments,
        string stdoutPath,
        string stderrPath,
        Func<string, string> quote)
    {
        var command = string.Join(' ', arguments.Select(quote));
        return $"{command} > {quote(stdoutPath)} 2> {quote(stderrPath)}";
    }

    private static string QuoteForCmd(string value) =>
        $"\"{value.Replace("\"", "\\\"", StringComparison.Ordinal)}\"";

    private static string QuoteForPosix(string value) =>
        $"'{value.Replace("'", "'\\''", StringComparison.Ordinal)}'";

    private static async Task<string> ReadFileWithRetryAsync(string path)
    {
        // A reparented grandchild may still hold the file's write handle; open shared and tolerate
        // transient locks. The output we need (the child's own writes) is already flushed on exit.
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                using var stream = new FileStream(
                    path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                return await reader.ReadToEndAsync().ConfigureAwait(false);
            }
            catch (FileNotFoundException)
            {
                return string.Empty;
            }
            catch (IOException)
            {
                await Task.Delay(100).ConfigureAwait(false);
            }
        }

        return string.Empty;
    }

    private static void TryDeleteFile(string path)
    {
        try { File.Delete(path); } catch { /* best effort; lives under the temp dir */ }
    }

    private sealed record PolicyShardPlan(
        bool Applies,
        bool ForceFull,
        string Evidence,
        IReadOnlySet<string> DependencyClosure)
    {
        public static PolicyShardPlan NotApplicable(string evidence) =>
            new(false, false, evidence, new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        public static PolicyShardPlan Full(string evidence, IReadOnlySet<string> dependencyClosure) =>
            new(true, true, evidence, dependencyClosure);

        public static PolicyShardPlan Scoped(string evidence, IReadOnlySet<string> dependencyClosure) =>
            new(true, false, evidence, dependencyClosure);

        public bool IncludesProject(string? project) =>
            ForceFull ||
            !Applies ||
            (!string.IsNullOrWhiteSpace(project) &&
                DependencyClosure.Contains(NormalizePath(project)!));
    }

    private sealed class AcceptanceManifest
    {
        private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

        public int Version { get; init; } = 1;
        public IReadOnlyList<AcceptanceManifestCheck> Checks { get; init; } = [AcceptanceManifestCheck.DefaultDotnetTest];
        public IReadOnlyList<string> ForbiddenChangedPathGlobs { get; init; } = [];

        public static AcceptanceManifest Load(string worktreePath, IReadOnlyList<string>? changedFiles)
        {
            var path = ResolveManifestPath(worktreePath);
            if (!File.Exists(path))
            {
                return changedFiles is null
                    ? new AcceptanceManifest()
                    : FromTestImpactPlan(RepositoryTestImpactPlanner.Plan(changedFiles));
            }

            return JsonSerializer.Deserialize<AcceptanceManifest>(
                File.ReadAllText(path),
                JsonOptions) ?? new AcceptanceManifest();
        }

        private static AcceptanceManifest FromTestImpactPlan(RepositoryTestImpactPlan plan) =>
            new()
            {
                Checks = plan.Checks
                    .Select(ToAcceptanceCheck)
                    .SelectMany(ExpandBroadInfrastructureCheck)
                    .ToArray()
            };

        private static AcceptanceManifestCheck ToAcceptanceCheck(RepositoryTestImpactCheck check)
        {
            if (check.Command.Count == 0)
            {
                return new AcceptanceManifestCheck
                {
                    Name = check.Name,
                    Type = "no-op"
                };
            }

            if (check.Command.Count >= 2 &&
                check.Command[0].Equals("dotnet", StringComparison.OrdinalIgnoreCase) &&
                check.Command[1].Equals("test", StringComparison.OrdinalIgnoreCase))
            {
                var remaining = check.Command.Skip(2).ToArray();
                var project = remaining.Length > 0 && !remaining[0].StartsWith("-", StringComparison.Ordinal)
                    ? remaining[0]
                    : null;
                var arguments = project is null
                    ? remaining
                    : remaining.Skip(1).ToArray();

                return new AcceptanceManifestCheck
                {
                    Name = check.Name,
                    Type = "dotnet-test",
                    Project = project,
                    Arguments = arguments
                };
            }

            return new AcceptanceManifestCheck
            {
                Name = check.Name,
                Type = "command",
                Command = check.Command[0],
                Arguments = check.Command.Skip(1).ToArray()
            };
        }

        private static string ResolveManifestPath(string worktreePath)
        {
            var trackedPath = Path.Combine(worktreePath, "config", "acceptance-manifest.json");
            if (File.Exists(trackedPath))
            {
                return trackedPath;
            }

            return Path.Combine(worktreePath, ".orchestrator", "acceptance-manifest.json");
        }
    }

    private static readonly JsonSerializerOptions CriteriaJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private sealed class AcceptanceManifestCheck
    {
        public static AcceptanceManifestCheck DefaultDotnetTest { get; } = new()
        {
            Name = "dotnet test",
            Type = "dotnet-test"
        };

        public string Name { get; init; } = "acceptance check";
        public string Type { get; init; } = "command";
        public string? Command { get; init; }
        public string? Project { get; init; }
        public IReadOnlyList<string> Arguments { get; init; } = [];
        public string? Pattern { get; init; }
        public string? FilePath { get; init; }
        public int? TimeoutMinutes { get; init; }
        public bool Advisory { get; init; }
    }

    private sealed record InfrastructureTestLane(string Name, string Filter);
}
