using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal interface IPostLandingCanaryRunner
{
    Task<PostLandingCanaryOutcome> RunAsync(
        PostLandingCanaryRequest request,
        CancellationToken cancellationToken);
}

internal sealed record PostLandingCanaryRepositoryVerdict(
    bool Green,
    bool PreconditionFailure,
    string Detail);

internal static class PostLandingCanaryRepositoryInvariant
{
    internal static PostLandingCanaryRepositoryVerdict Evaluate(
        string baselineSha,
        string currentSha,
        IReadOnlyList<string> baselineDirtyPaths,
        IReadOnlyList<string> currentDirtyPaths,
        bool currentIsDescendantOfBaseline)
    {
        var baselineDirty = FormatPaths(baselineDirtyPaths);
        var currentDirty = FormatPaths(currentDirtyPaths);
        var state =
            $"Baseline SHA: {baselineSha}. Current SHA: {currentSha}. " +
            $"Current is at or ahead of baseline: {currentIsDescendantOfBaseline}. " +
            $"Baseline dirty paths: {baselineDirty}. Current dirty paths: {currentDirty}.";

        if (baselineDirtyPaths.Count > 0)
        {
            return new PostLandingCanaryRepositoryVerdict(
                Green: false,
                PreconditionFailure: true,
                $"Post-landing canary repository precondition failed; its isolated checkout was dirty before the run. {state}");
        }

        if (!currentIsDescendantOfBaseline || currentDirtyPaths.Count > 0)
        {
            return new PostLandingCanaryRepositoryVerdict(
                Green: false,
                PreconditionFailure: false,
                $"Post-landing canary dirtied or rewrote its isolated checkout. {state}");
        }

        return new PostLandingCanaryRepositoryVerdict(
            Green: true,
            PreconditionFailure: false,
            $"Post-landing canary left its isolated checkout clean and at or ahead of its baseline. {state}");
    }

    private static string FormatPaths(IReadOnlyList<string> paths) =>
        paths.Count == 0 ? "<none>" : string.Join(", ", paths);
}

internal sealed class PostLandingCanaryRunner : IPostLandingCanaryRunner
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private readonly string _repositoryRoot;
    private readonly string _dotnetPath;
    private readonly string _buildCacheRoot;
    private readonly string _logDirectory;
    private readonly Func<PostLandingCanaryRequest, CancellationToken, Task<PostLandingCanaryOutcome>>? _override;

    internal PostLandingCanaryRunner(
        string repositoryRoot,
        string? dotnetPath = null,
        Func<PostLandingCanaryRequest, CancellationToken, Task<PostLandingCanaryOutcome>>? runOverride = null,
        string? buildCacheRoot = null,
        string? logDirectory = null)
    {
        _repositoryRoot = Path.GetFullPath(repositoryRoot);
        _dotnetPath = string.IsNullOrWhiteSpace(dotnetPath) ? "dotnet" : dotnetPath;
        _buildCacheRoot = string.IsNullOrWhiteSpace(buildCacheRoot)
            ? Path.Combine(Path.GetTempPath(), "mcg-post-landing-canary-build")
            : Path.GetFullPath(buildCacheRoot);
        _logDirectory = string.IsNullOrWhiteSpace(logDirectory)
            ? Path.Combine(_repositoryRoot, ".orchestrator", "logs")
            : Path.GetFullPath(logDirectory);
        _override = runOverride;
    }

    public Task<PostLandingCanaryOutcome> RunAsync(
        PostLandingCanaryRequest request,
        CancellationToken cancellationToken) =>
        _override is null
            ? RunCoreAsync(request, cancellationToken)
            : _override(request, cancellationToken);

    private async Task<PostLandingCanaryOutcome> RunCoreAsync(
        PostLandingCanaryRequest request,
        CancellationToken cancellationToken)
    {
        var logs = new PostLandingCanaryLogSession(_logDirectory, request.LandingSha);
        var canaryRepositoryRoot = await CreateIsolatedWorktreeAsync(request.LandingSha, logs, cancellationToken)
            .ConfigureAwait(false);
        Exception? runFailure = null;
        try
        {
            var baseline = await ReadRepositoryStateAsync(canaryRepositoryRoot, logs, cancellationToken)
                .ConfigureAwait(false);
            if (!baseline.HeadSha.Equals(request.LandingSha, StringComparison.OrdinalIgnoreCase))
            {
                throw new PostLandingCanaryPreconditionException(
                    $"Post-landing canary isolated worktree expected {request.LandingSha}, " +
                    $"but found {baseline.HeadSha}.");
            }

            var precondition = PostLandingCanaryRepositoryInvariant.Evaluate(
                baseline.HeadSha,
                baseline.HeadSha,
                baseline.DirtyPaths,
                baseline.DirtyPaths,
                currentIsDescendantOfBaseline: true);
            if (!precondition.Green)
            {
                throw CreateRepositoryFailureException(precondition);
            }

            using var fixture = PostLandingCanaryFixture.Materialize(canaryRepositoryRoot, request.LandingSha);
            await InitializeFixtureRepositoryAsync(fixture.RootPath, logs, cancellationToken).ConfigureAwait(false);
            var appDllPath = await ResolveOrBuildMainBinaryAsync(
                    canaryRepositoryRoot,
                    baseline.HeadSha,
                    logs,
                    cancellationToken)
                .ConfigureAwait(false);
            var process = await RunProcessAsync(
                _dotnetPath,
                [
                    appDllPath,
                    PostLandingCanaryCommand.SubcommandName,
                    fixture.RootPath
                ],
                canaryRepositoryRoot,
                logs,
                "run-canary-probe",
                cancellationToken).ConfigureAwait(false);

            var current = await ReadRepositoryStateAsync(canaryRepositoryRoot, logs, cancellationToken)
                .ConfigureAwait(false);
            var currentIsDescendant = await IsAncestorAsync(
                canaryRepositoryRoot,
                baseline.HeadSha,
                current.HeadSha,
                logs,
                cancellationToken).ConfigureAwait(false);
            var repositoryVerdict = PostLandingCanaryRepositoryInvariant.Evaluate(
                baseline.HeadSha,
                current.HeadSha,
                baseline.DirtyPaths,
                current.DirtyPaths,
                currentIsDescendant);
            if (!repositoryVerdict.Green)
            {
                throw CreateRepositoryFailureException(repositoryVerdict);
            }

            var probeLine = process.Stdout
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .LastOrDefault(line => line.StartsWith(PostLandingCanaryCommand.ResultPrefix, StringComparison.Ordinal));
            if (probeLine is null)
            {
                throw new PostLandingCanaryEvaluationException(
                    $"canary subprocess returned exit {process.ExitCode} without a result contract: {Tail(process.Stderr)}");
            }

            PostLandingCanaryProbeResult? probe;
            try
            {
                probe = JsonSerializer.Deserialize<PostLandingCanaryProbeResult>(
                    probeLine[PostLandingCanaryCommand.ResultPrefix.Length..],
                    JsonOptions);
            }
            catch (JsonException ex)
            {
                throw new PostLandingCanaryEvaluationException(
                    $"canary subprocess result contract was invalid: {ex.Message}");
            }

            if (probe is null)
            {
                throw new PostLandingCanaryEvaluationException(
                    "canary subprocess returned an empty result contract");
            }

            return probe.Green
                ? PostLandingCanaryOutcome.Passed(probe.ExecutedTestCount, probe.Detail)
                : PostLandingCanaryOutcome.Failed(
                    probe.FailureReason ?? PostLandingCanaryFailureReason.InfrastructureError,
                    probe.Detail,
                    probe.ExecutedTestCount);
        }
        catch (Exception ex)
        {
            runFailure = ex;
            throw;
        }
        finally
        {
            try
            {
                await RemoveIsolatedWorktreeAsync(canaryRepositoryRoot, logs).ConfigureAwait(false);
            }
            catch when (runFailure is not null)
            {
                // Preserve the primary canary failure; stale temporary worktrees are pruned by git maintenance.
            }
        }
    }

    internal static Exception CreateRepositoryFailureException(PostLandingCanaryRepositoryVerdict verdict)
    {
        if (verdict.Green)
        {
            throw new ArgumentException("A green repository verdict has no failure exception.", nameof(verdict));
        }

        return verdict.PreconditionFailure
            ? new PostLandingCanaryPreconditionException(verdict.Detail)
            : new PostLandingCanaryEvaluationException(verdict.Detail);
    }

    private async Task<string> CreateIsolatedWorktreeAsync(
        string landingSha,
        PostLandingCanaryLogSession logs,
        CancellationToken cancellationToken)
    {
        var worktreeRoot = Path.Combine(
            _repositoryRoot,
            ".orchestrator",
            "mcg-post-landing-canary-worktrees",
            landingSha[..Math.Min(12, landingSha.Length)],
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.GetDirectoryName(worktreeRoot)!);
        await EnsureSucceededAsync(
            "create isolated landing worktree",
            "git",
            ["-c", "core.longpaths=true", "-C", _repositoryRoot, "worktree", "add", "--detach", "--quiet", worktreeRoot, landingSha],
            _repositoryRoot,
            logs,
            cancellationToken).ConfigureAwait(false);
        return worktreeRoot;
    }

    private async Task RemoveIsolatedWorktreeAsync(
        string worktreeRoot,
        PostLandingCanaryLogSession logs)
    {
        await EnsureSucceededAsync(
            "remove isolated landing worktree",
            "git",
            ["-c", "core.longpaths=true", "-C", _repositoryRoot, "worktree", "remove", "--force", worktreeRoot],
            _repositoryRoot,
            logs,
            CancellationToken.None).ConfigureAwait(false);
    }

    private async Task InitializeFixtureRepositoryAsync(
        string fixtureRoot,
        PostLandingCanaryLogSession logs,
        CancellationToken cancellationToken)
    {
        await EnsureSucceededAsync(
            "initialize fixture repository",
            "git",
            ["init", "--quiet"],
            fixtureRoot,
            logs,
            cancellationToken).ConfigureAwait(false);
        await EnsureSucceededAsync(
            "stage fixture repository",
            "git",
            ["add", "--all"],
            fixtureRoot,
            logs,
            cancellationToken).ConfigureAwait(false);
        await EnsureSucceededAsync(
            "commit fixture repository",
            "git",
            [
                "-c", "user.name=MCG Canary",
                "-c", "user.email=canary@localhost",
                "commit", "--quiet", "-m", "known-green canary fixture"
            ],
            fixtureRoot,
            logs,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> ResolveOrBuildMainBinaryAsync(
        string sourceRoot,
        string sourceSha,
        PostLandingCanaryLogSession logs,
        CancellationToken cancellationToken)
    {
        var repositoryKey = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(_repositoryRoot)))[..16];
        var outputDirectory = Path.Combine(
            _buildCacheRoot,
            repositoryKey,
            sourceSha);
        var appDllPath = Path.Combine(outputDirectory, "Mcg.AgentOrchestrator.App.dll");
        var markerPath = appDllPath + ".git-head";
        if (File.Exists(appDllPath) &&
            File.Exists(markerPath) &&
            File.ReadAllText(markerPath).Trim().Equals(sourceSha, StringComparison.OrdinalIgnoreCase))
        {
            return appDllPath;
        }

        Directory.CreateDirectory(outputDirectory);
        await EnsureSucceededAsync(
            "build freshly landed main binary",
            _dotnetPath,
            [
                "build",
                Path.Combine(sourceRoot, "src", "Mcg.AgentOrchestrator.App", "Mcg.AgentOrchestrator.App.csproj"),
                "--nologo",
                "--output",
                outputDirectory,
                "-v",
                "quiet",
                "-clp:ErrorsOnly"
            ],
            sourceRoot,
            logs,
            cancellationToken,
            evaluatedArtifactFailure: true).ConfigureAwait(false);
        File.WriteAllText(markerPath, sourceSha + Environment.NewLine);
        return appDllPath;
    }

    private async Task<PostLandingCanaryRepositoryState> ReadRepositoryStateAsync(
        string repositoryRoot,
        PostLandingCanaryLogSession logs,
        CancellationToken cancellationToken)
    {
        var head = await RunProcessAsync(
            "git",
            ["-C", repositoryRoot, "rev-parse", "HEAD"],
            repositoryRoot,
            logs,
            "read-repository-head",
            cancellationToken).ConfigureAwait(false);
        if (head.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Post-landing canary could not resolve isolated checkout HEAD (exit {head.ExitCode}): {Tail(head.Stderr)}");
        }

        var status = await RunProcessAsync(
            "git",
            ["-C", repositoryRoot, "status", "--porcelain", "--untracked-files=all"],
            repositoryRoot,
            logs,
            "read-repository-status",
            cancellationToken).ConfigureAwait(false);
        if (status.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Post-landing canary could not inspect its isolated working tree (exit {status.ExitCode}): {Tail(status.Stderr)}");
        }

        return new PostLandingCanaryRepositoryState(
            head.Stdout.Trim(),
            GitCli.ParseCommitWorthyStatusPaths(status.Stdout));
    }

    private async Task<bool> IsAncestorAsync(
        string repositoryRoot,
        string expectedAncestor,
        string currentSha,
        PostLandingCanaryLogSession logs,
        CancellationToken cancellationToken)
    {
        var ancestry = await RunProcessAsync(
            "git",
            ["-C", repositoryRoot, "merge-base", "--is-ancestor", expectedAncestor, currentSha],
            repositoryRoot,
            logs,
            "check-repository-ancestry",
            cancellationToken).ConfigureAwait(false);
        if (ancestry.ExitCode is 0 or 1)
        {
            return ancestry.ExitCode == 0;
        }

        throw new InvalidOperationException(
            $"Post-landing canary could not evaluate ancestry from {expectedAncestor} to {currentSha} " +
            $"(exit {ancestry.ExitCode}): {Tail(ancestry.Stderr)}");
    }

    private static async Task EnsureSucceededAsync(
        string operation,
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        PostLandingCanaryLogSession logs,
        CancellationToken cancellationToken,
        bool evaluatedArtifactFailure = false)
    {
        var result = await RunProcessAsync(
            fileName,
            arguments,
            workingDirectory,
            logs,
            operation,
            cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            var detail =
                $"Failed to {operation} (exit {result.ExitCode}): {Tail(result.Stdout + Environment.NewLine + result.Stderr)}";
            throw evaluatedArtifactFailure
                ? new PostLandingCanaryEvaluationException(detail)
                : new InvalidOperationException(detail);
        }
    }

    private static async Task<PostLandingCanaryProcessResult> RunProcessAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        PostLandingCanaryLogSession logs,
        string operation,
        CancellationToken cancellationToken)
    {
        var captureToFiles = OperatingSystem.IsWindows();
        var (stdoutPath, stderrPath) = logs.CreateCaptureFiles(operation);
        var startInfo = captureToFiles
            ? BuildWindowsFileCaptureStartInfo(
                fileName,
                arguments,
                workingDirectory,
                stdoutPath,
                stderrPath)
            : new ProcessStartInfo(fileName)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.RedirectStandardInput = !captureToFiles;
        if (!captureToFiles)
        {
            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }
        }

        GoalAcceptanceVerifier.ConfigureHermeticVerificationEnvironment(startInfo.Environment, workingDirectory);
        using var process = WorkerProcessJobs.StartRegisteredOrThrow(
            startInfo,
            $"post-landing-canary:{workingDirectory}");
        if (startInfo.RedirectStandardInput)
        {
            process.StandardInput.Close();
        }

        try
        {
            // Do not cancel pipe drains before the killed process tree closes its handles.
            // Completion of this method is the coordinator's termination confirmation.
            var stdoutTask = captureToFiles
                ? Task.FromResult(string.Empty)
                : process.StandardOutput.ReadToEndAsync();
            var stderrTask = captureToFiles
                ? Task.FromResult(string.Empty)
                : process.StandardError.ReadToEndAsync();
            try
            {
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
                var stdout = captureToFiles
                    ? await GoalAcceptanceVerifier.ReadCapturedFileWithRetryAsync(stdoutPath, false)
                        .ConfigureAwait(false)
                    : await stdoutTask.ConfigureAwait(false);
                var stderr = captureToFiles
                    ? await GoalAcceptanceVerifier.ReadCapturedFileWithRetryAsync(stderrPath, false)
                        .ConfigureAwait(false)
                    : await stderrTask.ConfigureAwait(false);
                if (!captureToFiles)
                {
                    await PersistManagedCaptureAsync(stdoutPath, stdout, stderrPath, stderr).ConfigureAwait(false);
                }
                return new PostLandingCanaryProcessResult(
                    process.ExitCode,
                    stdout,
                    stderr);
            }
            catch (OperationCanceledException)
            {
                var killed = false;
                try { killed = WorkerProcessJobs.TryKillOrFallback(process.Id); } catch { }
                if (!killed)
                {
                    try { process.Kill(entireProcessTree: true); } catch { }
                }

                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
                if (!captureToFiles)
                {
                    await PersistManagedCaptureAsync(
                            stdoutPath,
                            await stdoutTask.ConfigureAwait(false),
                            stderrPath,
                            await stderrTask.ConfigureAwait(false))
                        .ConfigureAwait(false);
                }
                if (!process.HasExited)
                {
                    throw new InvalidOperationException(
                        $"Post-landing canary process tree rooted at pid {process.Id} did not terminate.");
                }

                throw;
            }
        }
        finally
        {
            WorkerProcessJobs.Release(process.Id);
        }
    }

    private static ProcessStartInfo BuildWindowsFileCaptureStartInfo(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        string stdoutPath,
        string stderrPath)
    {
        var command = string.Join(' ', new[] { fileName }
            .Concat(arguments)
            .Select(QuoteWindowsShellToken));
        command += $" > {QuoteWindowsShellToken(stdoutPath)} 2> {QuoteWindowsShellToken(stderrPath)}";
        return new ProcessStartInfo("cmd.exe")
        {
            Arguments = $"/d /s /c \"{command}\"",
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true
        };
    }

    private static string QuoteWindowsShellToken(string value)
    {
        if (value.Contains('"'))
        {
            throw new ArgumentException("Post-landing canary process arguments cannot contain double quotes.");
        }

        return value.Length == 0 || value.IndexOfAny([' ', '\t', '&', '|', '<', '>', '^', '(', ')']) >= 0
            ? $"\"{value}\""
            : value;
    }

    private static async Task PersistManagedCaptureAsync(
        string stdoutPath,
        string stdout,
        string stderrPath,
        string stderr)
    {
        await File.WriteAllTextAsync(stdoutPath, stdout, CancellationToken.None).ConfigureAwait(false);
        await File.WriteAllTextAsync(stderrPath, stderr, CancellationToken.None).ConfigureAwait(false);
    }

    private static string Tail(string value)
    {
        var normalized = (value ?? string.Empty).Trim();
        return normalized.Length <= 2000 ? normalized : normalized[^2000..];
    }

    private sealed record PostLandingCanaryProcessResult(int ExitCode, string Stdout, string Stderr);

    private sealed record PostLandingCanaryRepositoryState(
        string HeadSha,
        IReadOnlyList<string> DirtyPaths);

    private sealed class PostLandingCanaryLogSession
    {
        private readonly string _capturePrefix;
        private int _ordinal;

        internal PostLandingCanaryLogSession(string logDirectory, string landingSha)
        {
            Directory.CreateDirectory(logDirectory);
            _capturePrefix = Path.Combine(
                logDirectory,
                $"post-landing-canary-{SanitizeSegment(landingSha)}-" +
                $"{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}");
        }

        internal (string StdoutPath, string StderrPath) CreateCaptureFiles(string operation)
        {
            var ordinal = Interlocked.Increment(ref _ordinal);
            var captureStem = $"{_capturePrefix}-{ordinal:D2}-{SanitizeSegment(operation)}";
            var stdoutPath = captureStem + ".out.log";
            var stderrPath = captureStem + ".err.log";
            using (File.Create(stdoutPath)) { }
            using (File.Create(stderrPath)) { }
            return (stdoutPath, stderrPath);
        }

        private static string SanitizeSegment(string value)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var sanitized = new string(value
                .Select(character => invalid.Contains(character) ? '-' : character)
                .ToArray());
            return string.IsNullOrWhiteSpace(sanitized) ? "unknown" : sanitized;
        }
    }
}
