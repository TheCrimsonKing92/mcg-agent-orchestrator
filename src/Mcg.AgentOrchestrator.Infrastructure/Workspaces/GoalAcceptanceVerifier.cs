using System.Diagnostics;
using System.Text.Json;
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
    string? ResultSummary = null);

public sealed record AcceptanceVerificationResult(
    bool Passed,
    bool Skipped,
    int? ExitCode,
    string? OutputTail,
    bool Retried = false,
    string? ArtifactsPath = null,
    IReadOnlyList<AcceptanceCheckResult>? Checks = null);

public sealed class GoalAcceptanceVerifier
{
    internal sealed record CommandResult(int ExitCode, string Output);

    private static readonly TimeSpan CommandTimeout = TimeSpan.FromMinutes(10);

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
        var checks = new List<AcceptanceCheckResult>();
        var retried = false;
        foreach (var check in manifest.Checks)
        {
            var checkResult = await RunCheckAsync(check, worktreePath, goalId, cancellationToken).ConfigureAwait(false);
            retried |= checkResult.Retried;
            checks.Add(checkResult.Result);
            if (!checkResult.Result.Passed)
            {
                break;
            }
        }

        if (checks.All(check => check.Passed) && manifest.ForbiddenChangedPathGlobs.Count > 0)
        {
            checks.Add(await RunForbiddenChangedPathsCheckAsync(manifest.ForbiddenChangedPathGlobs, worktreePath, cancellationToken).ConfigureAwait(false));
        }

        var failedCheck = checks.FirstOrDefault(check => !check.Passed);
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

    private async Task<(AcceptanceCheckResult Result, bool Retried)> RunCheckAsync(
        AcceptanceManifestCheck check,
        string worktreePath,
        GoalId? goalId,
        CancellationToken cancellationToken)
    {
        if (check.Type.Equals("no-op", StringComparison.OrdinalIgnoreCase))
        {
            return (new AcceptanceCheckResult(check.Name, true, null, null), false);
        }

        return check.Type.Equals("dotnet-test", StringComparison.OrdinalIgnoreCase)
            ? await RunDotnetTestCheckAsync(check, worktreePath, goalId, cancellationToken).ConfigureAwait(false)
            : await RunCommandCheckAsync(check, worktreePath, goalId, cancellationToken).ConfigureAwait(false);
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
            result.ExitCode == 0 ? null : TailOutput(result.Output)), false);
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
        if (result.ExitCode != 0 && result.Output.Contains("CS2012", StringComparison.Ordinal))
        {
            // CS2012 is a transient file-lock on obj dlls; a second build-server shutdown
            // clears residual compiler processes before the single allowed retry.
            await _runner(["dotnet", "build-server", "shutdown"], worktreePath, cancellationToken).ConfigureAwait(false);
            environment = DotnetBuildEnvironmentManager.CreateAttempt(goalId, $"{attemptName}-retry");
            leaseLock.Dispose();
            using var retryLeaseLock = DotnetBuildEnvironmentManager.AcquireLeaseExecutionLock(environment, cancellationToken);
            result = await _runner(WithBuildEnvironmentArguments(arguments, environment), worktreePath, cancellationToken).ConfigureAwait(false);
            retried = true;
        }

        elapsed.Stop();
        return (new AcceptanceCheckResult(
            check.Name,
            result.ExitCode == 0,
            result.ExitCode,
            result.ExitCode == 0 ? null : TailOutput(result.Output),
            environment.ArtifactsPath,
            "goal-acceptance-verifier",
            environment.LeaseId,
            (long)elapsed.Elapsed.TotalMilliseconds,
            retried,
            ExtractResultSummary(result.Output)), retried);
    }

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

    private static string[] BuildDotnetTestArguments(AcceptanceManifestCheck check)
    {
        var args = new List<string> { "dotnet", "test" };
        if (!string.IsNullOrWhiteSpace(check.Project))
        {
            args.Add(check.Project);
        }

        args.AddRange(check.Arguments);
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
        var startInfo = new ProcessStartInfo
        {
            FileName = arguments[0],
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory
        };

        // Match the dispatch wrapper: without these, MSBuild worker nodes and
        // VBCSCompiler outlive the root test process holding the redirected
        // pipes and worktree obj files, so the output reads below hang until
        // the command timeout cancels them.
        startInfo.EnvironmentVariables["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0";
        startInfo.EnvironmentVariables["MSBUILDDISABLENODEREUSE"] = "1";
        startInfo.EnvironmentVariables["UseSharedCompilation"] = "false";
        startInfo.EnvironmentVariables["MCG_ORCHESTRATOR_REPOSITORY_ROOT"] = workingDirectory;

        for (var i = 1; i < arguments.Length; i++)
        {
            startInfo.ArgumentList.Add(arguments[i]);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Failed to start process: {arguments[0]}");

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(CommandTimeout);

        var stdoutTask = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
        var stderrTask = process.StandardError.ReadToEndAsync(timeoutCts.Token);

        await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);

        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);
        return new CommandResult(process.ExitCode, (stdout + stderr).Trim());
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
    }
}
