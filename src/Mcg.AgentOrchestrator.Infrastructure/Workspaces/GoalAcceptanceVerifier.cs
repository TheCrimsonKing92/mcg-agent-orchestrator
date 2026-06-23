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
        CancellationToken cancellationToken = default);
}

public sealed class GoalAcceptanceVerifier : IGoalAcceptanceVerifier
{
    internal sealed record CommandResult(int ExitCode, string Output);

    // Hard ceiling for a single build/test process. The suite itself runs in ~90s even in the
    // throttled acceptance environment, so this only guards a genuinely runaway process. Output is
    // captured to files (see RunProcessAsync) so a grandchild holding an inherited handle no longer
    // stalls the command to this ceiling.
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromMinutes(10);

    private static readonly Regex TestAttrPattern = new(
        @"^\[(?:Fact|Theory|Xunit\.Fact\()",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex TautologyPattern = new(
        @"Assert\.True\(\s*true\s*\)|Assert\.False\(\s*false\s*\)|Assert\.Equal\(\s*(?<v>\w+)\s*,\s*\k<v>\s*\)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly string[] DiffBaseArgs = ["git", "diff", "--unified=0", "main...HEAD", "--"];

    private readonly Func<string[], string, CancellationToken, Task<CommandResult>> _runner;

    public GoalAcceptanceVerifier() : this(RunProcessAsync) { }

    internal GoalAcceptanceVerifier(Func<string[], string, CancellationToken, Task<CommandResult>> runner)
    {
        _runner = runner;
    }

    public async Task<AcceptanceVerificationResult> RunAsync(
        string worktreePath,
        GoalId? goalId = null,
        IReadOnlyList<string>? changedFiles = null,
        CancellationToken cancellationToken = default)
    {
        // Shut down build servers to release file locks before running tests.
        await _runner(["dotnet", "build-server", "shutdown"], worktreePath, cancellationToken).ConfigureAwait(false);

        var manifest = AcceptanceManifest.Load(worktreePath, changedFiles);

        // Inject any policy-required checks (derived from the change scope) that are not
        // already present in the manifest, so the anti-drift gate never fires from a stale
        // manifest without the operator needing to hand-edit acceptance-manifest.json.
        var injected = BuildPolicyInjectedChecks(manifest.Checks, changedFiles);
        IReadOnlyList<AcceptanceManifestCheck> effectiveChecks = injected.Count == 0
            ? manifest.Checks
            : [.. manifest.Checks, .. injected];

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

        var scopedChecks = BuildChangeScopedChecks(solutionCheck, effectiveChecks, changedFiles);
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
                var checkResult = await RunCheckAsync(check, worktreePath, goalId, cancellationToken).ConfigureAwait(false);
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
                    var checkResult = await RunCheckAsync(check, worktreePath, goalId, cancellationToken).ConfigureAwait(false);
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
                var checkResult = await RunCheckAsync(check, worktreePath, goalId, cancellationToken).ConfigureAwait(false);
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
                var slnRun = await RunCheckAsync(solutionCheck!, worktreePath, goalId, cancellationToken).ConfigureAwait(false);
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
                        var checkResult = await RunCheckAsync(check, worktreePath, goalId, cancellationToken).ConfigureAwait(false);
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
                var checkResult = await RunCheckAsync(check, worktreePath, goalId, cancellationToken).ConfigureAwait(false);
                retried |= checkResult.Retried;
                checks.Add(checkResult.Result);
                if (!checkResult.Result.Passed)
                {
                    break;
                }
            }
        }

        if (checks.All(check => check.Passed) && manifest.ForbiddenChangedPathGlobs.Count > 0)
        {
            checks.Add(await RunForbiddenChangedPathsCheckAsync(manifest.ForbiddenChangedPathGlobs, worktreePath, cancellationToken).ConfigureAwait(false));
        }

        // Advisory checks: always run, failures are recorded but do not affect overall Passed.
        foreach (var advisoryCheck in advisoryChecks)
        {
            var checkResult = await RunCheckAsync(advisoryCheck, worktreePath, goalId, cancellationToken).ConfigureAwait(false);
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
        IReadOnlyList<string>? changedFiles)
    {
        if (solutionCheck is null ||
            changedFiles is null ||
            changedFiles.Count == 0 ||
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
            var existing = allChecks.FirstOrDefault(check =>
                check.Type.Equals("dotnet-test", StringComparison.OrdinalIgnoreCase) &&
                !check.Name.Equals(solutionCheck.Name, StringComparison.Ordinal) &&
                DotnetCheckMatches(check, plannedManifestCheck));
            scoped.Add(existing ?? plannedManifestCheck);
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

    private static List<AcceptanceManifestCheck> BuildPolicyInjectedChecks(
        IReadOnlyList<AcceptanceManifestCheck> manifestChecks,
        IReadOnlyList<string>? changedFiles)
    {
        if (changedFiles is null || changedFiles.Count == 0)
            return [];

        var plan = RepositoryTestImpactPlanner.Plan(changedFiles);
        var coveredNames = manifestChecks
            .Select(c => c.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var injected = new List<AcceptanceManifestCheck>();
        var injectedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var check in plan.Checks.Where(c => c.Command.Count > 0))
        {
            if (coveredNames.Contains(check.Name))
                continue;

            var commandKey = $"dotnet-test:{check.CommandLine}";
            if (!injectedKeys.Add(commandKey))
                continue;

            injected.Add(PolicyCheckToManifestCheck(check));
        }

        return injected;
    }

    private static AcceptanceManifestCheck PolicyCheckToManifestCheck(RepositoryTestImpactCheck check)
    {
        // Command format: ["dotnet", "test", <optional project>, ...args]
        var remaining = check.Command.Skip(2).ToArray();
        var project = remaining.Length > 0 && !remaining[0].StartsWith("-", StringComparison.Ordinal)
            ? remaining[0]
            : null;
        var arguments = project is null ? remaining : remaining.Skip(1).ToArray();
        return new AcceptanceManifestCheck
        {
            Name = check.Name,
            Type = "dotnet-test",
            Project = project,
            Arguments = arguments
        };
    }

    private static bool IsProjectInSolution(string projectPath, string? solutionProject, string? slnContent)
    {
        if (string.IsNullOrWhiteSpace(solutionProject))
            return true;

        if (slnContent is null)
            return true;

        return slnContent.Contains(Path.GetFileName(projectPath), StringComparison.OrdinalIgnoreCase);
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
            return await RunCommandCheckAsync(check, worktreePath, goalId, cancellationToken).ConfigureAwait(false);

        return check.Type.Equals("dotnet-test", StringComparison.OrdinalIgnoreCase)
            ? await RunDotnetTestCheckAsync(check, worktreePath, goalId, cancellationToken).ConfigureAwait(false)
            : await RunCommandCheckAsync(check, worktreePath, goalId, cancellationToken).ConfigureAwait(false);
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
                $"acceptance-{Slug(check.Name)}",
                cancellationToken).ConfigureAwait(false);
        }

        var result = await _runner(arguments, worktreePath, cancellationToken).ConfigureAwait(false);
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
        CancellationToken cancellationToken)
    {
        return await RunManagedDotnetCheckAsync(
            check,
            BuildDotnetTestArguments(check),
            worktreePath,
            goalId,
            $"acceptance-{Slug(check.Name)}",
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<(AcceptanceCheckResult Result, bool Retried)> RunManagedDotnetCheckAsync(
        AcceptanceManifestCheck check,
        string[] arguments,
        string worktreePath,
        GoalId? goalId,
        string attemptName,
        CancellationToken cancellationToken)
    {
        var elapsed = Stopwatch.StartNew();
        var environment = DotnetBuildEnvironmentManager.CreateAttempt(goalId, attemptName);
        using var leaseLock = DotnetBuildEnvironmentManager.AcquireLeaseExecutionLock(environment, cancellationToken);
        var result = await _runner(WithBuildEnvironmentArguments(arguments, environment), worktreePath, cancellationToken).ConfigureAwait(false);

        var retried = false;
        if (result.ExitCode != 0 && (result.Output.Contains("CS2012", StringComparison.Ordinal) || IsTransientTesthostAbort(result.Output)))
        {
            // CS2012 is a transient obj-dll file lock; a mid-run testhost abort ("host process exited
            // unexpectedly" / "Test Run Aborted" with no completed verdict) is an environmental crash
            // (resource pressure, concurrent slot use). Both are transient: a second build-server shutdown
            // + fresh attempt clears them before the single allowed retry, so a one-off crash stops
            // spuriously escalating an otherwise-green goal.
            await _runner(["dotnet", "build-server", "shutdown"], worktreePath, cancellationToken).ConfigureAwait(false);
            environment = DotnetBuildEnvironmentManager.CreateAttempt(goalId, $"{attemptName}-retry");
            leaseLock.Dispose();
            using var retryLeaseLock = DotnetBuildEnvironmentManager.AcquireLeaseExecutionLock(environment, cancellationToken);
            result = await _runner(WithBuildEnvironmentArguments(arguments, environment), worktreePath, cancellationToken).ConfigureAwait(false);
            retried = true;
        }

        elapsed.Stop();
        // A testhost can exit non-zero on SHUTDOWN ("host process exited unexpectedly") even after every
        // test passed. Honor the run's own Passed!/Failed:0 summary so a benign shutdown abort does not
        // block a green goal, while never masking a build/compile failure and still surfacing the tail.
        var reportedAllPassed = result.ExitCode != 0 && TestRunReportsAllPassed(result.Output);
        var passed = result.ExitCode == 0 || reportedAllPassed;
        return (new AcceptanceCheckResult(
            check.Name,
            passed,
            result.ExitCode,
            passed && result.ExitCode == 0 ? null : TailOutput(result.Output),
            environment.ArtifactsPath,
            "goal-acceptance-verifier",
            environment.LeaseId,
            (long)elapsed.Elapsed.TotalMilliseconds,
            retried,
            ExtractResultSummary(result.Output)), retried);
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

    // A testhost that crashes MID-run ("host process exited unexpectedly" / "Test Run Aborted") with no
    // completed all-passed banner and no real test failure is an environmental abort, not a verdict — it
    // should be retried once like CS2012. A build/compile failure, or a completed run WITH real test
    // failures, is NOT this and must not be retried (it is a genuine red).
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
        var result = await _runner(["git", "diff", "--name-only", "main...HEAD"], worktreePath, cancellationToken).ConfigureAwait(false);
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

        var result = await _runner(diffArgs, worktreePath, cancellationToken).ConfigureAwait(false);

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

        args.AddRange(check.Arguments);

        // Exclude host-integration tests that spawn a real Kestrel dashboard server (binds a port,
        // needs an interactive firewall allow) — they hang in the unattended, relocated gate. Match
        // both by class name (works on a worktree built before the trait existed) and by the
        // [Trait("Category","HostIntegration")] tag (covers any future such tests). They run in a
        // dedicated lane instead.
        args.Add("--filter");
        args.Add("FullyQualifiedName!~DashboardHostTests&Category!=HostIntegration");

        // Fail a hung test fast and by name instead of silently eating CommandTimeout. A test that
        // spawns a process which blocks (e.g. on a firewall prompt) and then WaitForExit()s on it
        // can otherwise stall the whole acceptance for ten minutes ("A task was canceled"). The
        // inactivity timeout is per-test; the full suite runs in ~90s so this never false-trips.
        args.Add("--blame-hang-timeout");
        args.Add("120s");
        args.Add("--blame-hang-dump-type");
        args.Add("none");
        return [.. args];
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

    private static async Task<CommandResult> RunProcessAsync(
        string[] arguments,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        // Capture output to FILES via the platform shell, not pipes. A test or build can spawn a
        // grandchild that inherits the child's stdout/stderr handle and outlives it; with a
        // redirected PIPE the test runner never reaches EOF while that grandchild holds the write
        // end, so `dotnet test` never exits and the whole command rides CommandTimeout to a
        // "A task was canceled". A plain `dotnet test > out 2> err` exits cleanly in that same
        // scenario, so we mirror it: every process exits regardless of a lingering grandchild and
        // we read the files afterward with a shared, delete-tolerant handle.
        var stdoutPath = Path.Combine(Path.GetTempPath(), $"mcg-acc-{Guid.NewGuid():N}.out");
        var stderrPath = Path.Combine(Path.GetTempPath(), $"mcg-acc-{Guid.NewGuid():N}.err");

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

        int? startedProcessId = null;
        try
        {
            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException($"Failed to start process: {arguments[0]}");
            startedProcessId = process.Id;
            WorkerProcessJobs.TryRegister(process, $"acceptance:{workingDirectory}");

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(CommandTimeout);

            try
            {
                await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { WorkerProcessJobs.TryKillOrFallback(process.Id); } catch { /* best effort */ }
                try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
                throw;
            }

            var stdout = await ReadFileWithRetryAsync(stdoutPath).ConfigureAwait(false);
            var stderr = await ReadFileWithRetryAsync(stderrPath).ConfigureAwait(false);
            return new CommandResult(process.ExitCode, (stdout + stderr).Trim());
        }
        finally
        {
            if (startedProcessId is { } processId)
            {
                WorkerProcessJobs.Release(processId);
            }

            TryDeleteFile(stdoutPath);
            TryDeleteFile(stderrPath);
        }
    }

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
                Checks = plan.Checks.Select(ToAcceptanceCheck).ToArray()
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
        public bool Advisory { get; init; }
    }
}
