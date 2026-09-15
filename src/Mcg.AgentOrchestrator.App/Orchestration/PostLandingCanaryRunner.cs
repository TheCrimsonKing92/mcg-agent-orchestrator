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

internal interface IPostLandingCanaryApplicationBinaryResolver
{
    Task<string> ResolveAsync(
        string sourceRoot,
        string sourceSha,
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
    private readonly IPostLandingCanaryApplicationBinaryResolver? _applicationBinaryResolver;

    internal PostLandingCanaryRunner(
        string repositoryRoot,
        string? dotnetPath = null,
        Func<PostLandingCanaryRequest, CancellationToken, Task<PostLandingCanaryOutcome>>? runOverride = null,
        string? buildCacheRoot = null,
        string? logDirectory = null,
        IPostLandingCanaryApplicationBinaryResolver? applicationBinaryResolver = null)
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
        _applicationBinaryResolver = applicationBinaryResolver;
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
        var canaryBuildEnvironmentRoot = Path.Combine(
            Path.GetTempPath(),
            $"mcg-pc-{Environment.ProcessId}-{Guid.NewGuid():N}"[..24]);
        Exception? runFailure = null;
        try
        {
            Directory.CreateDirectory(canaryBuildEnvironmentRoot);
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
            var appDllPath = _applicationBinaryResolver is null
                ? await ResolveOrBuildMainBinaryAsync(
                        canaryRepositoryRoot,
                        baseline.HeadSha,
                        logs,
                        cancellationToken)
                    .ConfigureAwait(false)
                : await _applicationBinaryResolver.ResolveAsync(
                        canaryRepositoryRoot,
                        baseline.HeadSha,
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
                cancellationToken,
                logs.ReceiptPrefix,
                canaryBuildEnvironmentRoot).ConfigureAwait(false);

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
                ? PostLandingCanaryOutcome.Passed(probe.ExecutedTestCount, probe.Detail, probe.SlotResolution)
                : PostLandingCanaryOutcome.Failed(
                    probe.FailureReason ?? PostLandingCanaryFailureReason.InfrastructureError,
                    probe.Detail,
                    probe.ExecutedTestCount,
                    probe.SlotResolution);
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
            finally
            {
                try
                {
                    if (Directory.Exists(canaryBuildEnvironmentRoot))
                    {
                        Directory.Delete(canaryBuildEnvironmentRoot, recursive: true);
                    }
                }
                catch when (runFailure is not null)
                {
                    // Preserve the primary canary failure; the isolated root contains only run-scoped build state.
                }
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
            Path.GetTempPath(),
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
            ["-c", "core.longpaths=true", "init", "--quiet"],
            fixtureRoot,
            logs,
            cancellationToken).ConfigureAwait(false);
        await EnsureSucceededAsync(
            "stage fixture repository",
            "git",
            ["-c", "core.longpaths=true", "add", "--all"],
            fixtureRoot,
            logs,
            cancellationToken).ConfigureAwait(false);
        await EnsureSucceededAsync(
            "commit fixture repository",
            "git",
            [
                "-c", "core.longpaths=true",
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
            ["-c", "core.longpaths=true", "-C", repositoryRoot, "rev-parse", "HEAD"],
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
            ["-c", "core.longpaths=true", "-C", repositoryRoot, "status", "--porcelain", "--untracked-files=all"],
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
            ["-c", "core.longpaths=true", "-C", repositoryRoot, "merge-base", "--is-ancestor", expectedAncestor, currentSha],
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

    /// <param name="inheritableWindowObserver">
    /// Per-launch hook invoked immediately before CreateProcessW, while this launch's inheritable
    /// capture duplicates exist. Only a test supplies it; no production caller does.
    /// </param>
    internal static async Task<(int ExitCode, string Stdout, string Stderr)> RunProcessForTestsAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        string logDirectory,
        string operation,
        CancellationToken cancellationToken,
        Action<IReadOnlyList<int>>? descendantObservation = null,
        Action? inheritableWindowObserver = null)
    {
        var result = await RunProcessAsync(
                fileName,
                arguments,
                workingDirectory,
                new PostLandingCanaryLogSession(logDirectory, "test-candidate"),
                operation,
                cancellationToken,
                descendantObservation: descendantObservation,
                inheritableWindowObserver: inheritableWindowObserver)
            .ConfigureAwait(false);
        return (result.ExitCode, result.Stdout, result.Stderr);
    }

    private static async Task<PostLandingCanaryProcessResult> RunProcessAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        PostLandingCanaryLogSession logs,
        string operation,
        CancellationToken cancellationToken,
        string? acceptanceAttemptResultsPrefix = null,
        string? dotnetIsolatedRoot = null,
        Action<IReadOnlyList<int>>? descendantObservation = null,
        Action? inheritableWindowObserver = null)
    {
        var nativeFileCapture = OperatingSystem.IsWindows();
        var (stdoutPath, stderrPath) = logs.CreateCaptureFiles(operation);
        var startInfo = BuildFileCaptureStartInfo(
            fileName,
            arguments,
            workingDirectory,
            redirectStandardStreams: !nativeFileCapture);

        GoalAcceptanceVerifier.ConfigureHermeticVerificationEnvironment(startInfo.Environment, workingDirectory);
        if (!string.IsNullOrWhiteSpace(acceptanceAttemptResultsPrefix))
        {
            if (string.IsNullOrWhiteSpace(dotnetIsolatedRoot))
            {
                throw new InvalidOperationException("A canary probe launch requires an isolated dotnet build root.");
            }

            // Set this after hermetic cleanup: it is a run-scoped output contract, not ambient operator state.
            startInfo.Environment[GoalAcceptanceVerifier.AcceptanceAttemptTrxPrefixVariable] =
                Path.GetFullPath(acceptanceAttemptResultsPrefix);
            startInfo.Environment[DotnetBuildEnvironmentManager.IsolatedRootOverrideVariable] =
                Path.GetFullPath(dotnetIsolatedRoot);
        }
        using var process = nativeFileCapture
            ? WorkerProcessJobs.StartRegisteredWithFileCaptureOrThrow(
                startInfo,
                stdoutPath,
                stderrPath,
                $"post-landing-canary:{workingDirectory}",
                inheritableWindowObserver)
            : WorkerProcessJobs.StartRegisteredOrThrow(
                startInfo,
                $"post-landing-canary:{workingDirectory}");
        if (startInfo.RedirectStandardInput)
        {
            process.StandardInput.Close();
        }

        var processId = process.Id;
        var processExitTask = process.WaitForExitAsync(CancellationToken.None);
        WorkerProcessJobReleaseEvidence? releaseEvidence = null;
        Task<IReadOnlyList<ProcessInspectionRecord>?>? descendantObservationTask = null;
        IReadOnlyList<ProcessInspectionRecord>? observedDescendantIdentities = [];
        var exitCode = 0;
        try
        {
            descendantObservationTask = nativeFileCapture
                ? ObserveIdentityBoundDescendantsUntilExitAsync(processId, processExitTask, descendantObservation)
                : null;
            // Do not cancel pipe drains before the killed process tree closes its handles.
            // Completion of this method is the coordinator's termination confirmation.
            var stdoutTask = nativeFileCapture
                ? Task.CompletedTask
                : CopyCapturedOutputAsync(process.StandardOutput, stdoutPath);
            var stderrTask = nativeFileCapture
                ? Task.CompletedTask
                : CopyCapturedOutputAsync(process.StandardError, stderrPath);
            try
            {
                await processExitTask.WaitAsync(cancellationToken).ConfigureAwait(false);
                await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
                if (descendantObservationTask is not null)
                {
                    observedDescendantIdentities = await descendantObservationTask.ConfigureAwait(false);
                }

                exitCode = process.ExitCode;
            }
            catch (OperationCanceledException)
            {
                var killed = false;
                try { killed = WorkerProcessJobs.TryKillOrFallback(process.Id); } catch { }
                if (!killed)
                {
                    try { process.Kill(entireProcessTree: true); } catch { }
                }

                await processExitTask.ConfigureAwait(false);
                await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
                if (descendantObservationTask is not null)
                {
                    observedDescendantIdentities = await descendantObservationTask.ConfigureAwait(false);
                }

                throw;
            }
        }
        finally
        {
            if (nativeFileCapture)
            {
                releaseEvidence = WorkerProcessJobs.ReleaseWithCompletionEvidence(processId, out _);
            }
            else
            {
                WorkerProcessJobs.Release(processId);
            }
        }

        if (nativeFileCapture)
        {
            var survivingDescendantProcessIds = ListStillLiveIdentityBoundProcessIds(observedDescendantIdentities);
            // The process has exited and its job is released: a token firing now must not turn a
            // completed run with readable captures into a cancellation the coordinator reads as a timeout.
            var stdoutObservation = await PostLandingCanaryCaptureAvailability.AwaitReadableAsync(
                    stdoutPath,
                    CancellationToken.None)
                .ConfigureAwait(false);
            EnsureRetainedCaptureReadable(
                stdoutPath,
                stdoutObservation,
                releaseEvidence!,
                survivingDescendantProcessIds);
            var stderrObservation = await PostLandingCanaryCaptureAvailability.AwaitReadableAsync(
                    stderrPath,
                    CancellationToken.None)
                .ConfigureAwait(false);
            EnsureRetainedCaptureReadable(
                stderrPath,
                stderrObservation,
                releaseEvidence!,
                survivingDescendantProcessIds);
        }

        var stdout = await GoalAcceptanceVerifier.ReadCapturedFileWithRetryAsync(stdoutPath, false)
            .ConfigureAwait(false);
        var stderr = await GoalAcceptanceVerifier.ReadCapturedFileWithRetryAsync(stderrPath, false)
            .ConfigureAwait(false);
        return new PostLandingCanaryProcessResult(exitCode, stdout, stderr);
    }

    /// <summary>
    /// A retained capture is complete only when the runner can open it exclusively AND every process it
    /// launched is proven released. A refused exclusive open is never accepted, whoever holds the file:
    /// naming the holder explains the failure, it does not excuse it. Consumers read the retained logs
    /// with File.ReadAllText, which opens FileShare.Read and throws while any foreign write handle
    /// lives, so a broad-sharing read here would prove nothing about what they will see.
    /// </summary>
    /// <param name="readHolders">
    /// The holder read used to name the processes in the failure message. Defaults to the real
    /// <see cref="FileHandleHolders"/> read; a test passes its own. It is invoked once, synchronously,
    /// on the failure path only, and adds no wait budget.
    /// </param>
    internal static void EnsureRetainedCaptureReadable(
        string path,
        RetainedCaptureObservation observation,
        WorkerProcessJobReleaseEvidence releaseEvidence,
        IReadOnlyList<int>? survivingDescendantProcessIds,
        Func<string, FileHandleHolderObservation>? readHolders = null)
    {
        var ownershipProven =
            releaseEvidence.RegistrationFound &&
            releaseEvidence.TerminationRequested &&
            releaseEvidence.JobExitConfirmed &&
            survivingDescendantProcessIds is { Count: 0 };

        if (observation.ExclusiveOpenSucceeded && ownershipProven)
        {
            return;
        }

        var holders = observation.Holders ?? (readHolders ?? FileHandleHolders.Read).Invoke(path);
        throw new InvalidOperationException(
            $"retained-capture-incomplete: path={path}; " +
            $"exclusive-open={observation.ExclusiveOpenSucceeded.ToString().ToLowerInvariant()}; " +
            $"attempts={observation.Attempts}; elapsed-ms={observation.Elapsed.TotalMilliseconds:F0}; " +
            $"last-error={observation.LastErrorCode ?? "unknown"}; " +
            $"registration-found={releaseEvidence.RegistrationFound.ToString().ToLowerInvariant()}; " +
            $"termination-requested={releaseEvidence.TerminationRequested.ToString().ToLowerInvariant()}; " +
            $"job-exit-confirmed={releaseEvidence.JobExitConfirmed.ToString().ToLowerInvariant()}; " +
            $"surviving-descendants={FormatProcessIds(survivingDescendantProcessIds)}; " +
            $"capture-holders={holders.Format()}; " +
            $"stable-length={FormatStableLength(observation)}");
    }

    private static string FormatProcessIds(IReadOnlyList<int>? processIds) =>
        processIds is null ? "observation-failed" :
        processIds.Count == 0 ? "none" : string.Join(',', processIds);

    private static string FormatStableLength(RetainedCaptureObservation observation) =>
        observation.FirstObservedLength is not { } first || observation.LastObservedLength is not { } last
            ? "unknown"
            : observation.LengthWasStable ? $"stable({first})" : $"grew({first}->{last})";

    /// <summary>
    /// Observes the launched process's identity-bound descendants until it exits. Returns null when any
    /// poll failed to observe: an unreadable poll leaves an unknown descendant set, and an unknown set is
    /// not an empty one. Swallowing the failure and returning no descendants would let
    /// <see cref="EnsureRetainedCaptureReadable"/> read "nothing survives" out of "nothing was seen" and
    /// call ownership proven, so the failure is surfaced instead and the run is reported incomplete.
    /// </summary>
    private static async Task<IReadOnlyList<ProcessInspectionRecord>?> ObserveIdentityBoundDescendantsUntilExitAsync(
        int processId,
        Task processExitTask,
        Action<IReadOnlyList<int>>? descendantObservation)
    {
        var observed = new Dictionary<int, ProcessInspectionRecord>();
        var observationFailed = false;
        var polls = 0;
        while (!processExitTask.IsCompleted)
        {
            try
            {
                var inspection = WindowsNativeProcessInspection.ReadIdentityBoundDescendants(processId);
                var processIds = inspection.Records.Keys.OrderBy(descendantId => descendantId).ToArray();
                descendantObservation?.Invoke(processIds);
                if (inspection.Failure is null)
                {
                    foreach (var record in inspection.Records.Values)
                    {
                        observed.TryAdd(record.ProcessId, record);
                    }
                }
                else
                {
                    observationFailed = true;
                    Console.Error.WriteLine(
                        $"canary-descendant-observation-failed: pid={processId}; failure={inspection.Failure}");
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                observationFailed = true;
                Console.Error.WriteLine(
                    $"canary-descendant-observation-failed: pid={processId}; error={exception.GetType().Name}");
            }

            polls++;
            if (!processExitTask.IsCompleted)
            {
                await Task.Delay(DescendantPollInterval(polls), CancellationToken.None).ConfigureAwait(false);
            }
        }

        await processExitTask.ConfigureAwait(false);
        return observationFailed ? null : observed.Values.ToArray();
    }

    /// <summary>
    /// Each poll is a full system process enumeration plus a per-tree-member handle read; this host
    /// reports ~1270 processes per enumeration. Descendants are spawned early, so the dense window that
    /// catches them is kept and the sustained cost over a multi-minute build launch is backed off
    /// instead of held at 20 enumerations per second for the whole run.
    /// </summary>
    private static TimeSpan DescendantPollInterval(int polls) =>
        polls < 20 ? TimeSpan.FromMilliseconds(50) :
        polls < 40 ? TimeSpan.FromMilliseconds(250) :
        TimeSpan.FromMilliseconds(1000);

    /// <summary>
    /// Re-checks each observed descendant identity after exit. Null in means the observation itself
    /// failed, and null out means the surviving set is unknown, which
    /// <see cref="EnsureRetainedCaptureReadable"/> treats as ownership unproven.
    /// </summary>
    private static IReadOnlyList<int>? ListStillLiveIdentityBoundProcessIds(
        IReadOnlyList<ProcessInspectionRecord>? expectedIdentities)
    {
        if (expectedIdentities is null)
        {
            return null;
        }

        if (expectedIdentities.Count == 0)
        {
            return [];
        }

        var surviving = new List<int>();
        foreach (var expected in expectedIdentities)
        {
            try
            {
                using var current = Process.GetProcessById(expected.ProcessId);
                if (current.HasExited)
                {
                    continue;
                }

                var currentStartedAt = new DateTimeOffset(current.StartTime.ToUniversalTime());
                if (expected.StartedAt.HasValue &&
                    expected.StartedAt.Value == currentStartedAt &&
                    !current.HasExited)
                {
                    surviving.Add(expected.ProcessId);
                }
            }
            catch (ArgumentException)
            {
                // The observed identity has exited and its pid is not currently assigned.
            }
            catch (InvalidOperationException)
            {
                // The observed identity exited while its handle-backed state was being read.
            }
            catch (System.ComponentModel.Win32Exception)
            {
                return null;
            }
        }

        return surviving.Distinct().OrderBy(processId => processId).ToArray();
    }

    internal static ProcessStartInfo BuildFileCaptureStartInfo(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        bool redirectStandardStreams = true)
    {
        if (OperatingSystem.IsWindows() &&
            Path.GetExtension(fileName) is var extension &&
            (extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase) ||
             extension.Equals(".bat", StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException(
                $"Post-landing canary cannot launch batch-file target '{fileName}' with extension " +
                $"'{extension}' through the shell-free owned-process path. Configure " +
                "MCG_ORCHESTRATOR_DOTNET_PATH to the underlying executable instead.");
        }

        var startInfo = new ProcessStartInfo(fileName)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = redirectStandardStreams,
            RedirectStandardOutput = redirectStandardStreams,
            RedirectStandardError = redirectStandardStreams
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return startInfo;
    }

    private static async Task CopyCapturedOutputAsync(StreamReader source, string destinationPath)
    {
        await using var destination = new FileStream(
            destinationPath,
            new FileStreamOptions
            {
                Mode = FileMode.Create,
                Access = FileAccess.Write,
                Share = FileShare.Read | FileShare.Delete,
                BufferSize = 1,
                Options = FileOptions.Asynchronous | FileOptions.WriteThrough
            });
        await source.BaseStream.CopyToAsync(destination, CancellationToken.None).ConfigureAwait(false);
        await destination.FlushAsync(CancellationToken.None).ConfigureAwait(false);
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
                Guid.NewGuid().ToString("N")[..12]);
        }

        // The verifier's generic owner-root discovery cannot identify this temporary Git worktree
        // (its .git marker is a file), so pin TRX output in the durable canary log directory that this
        // process already created and proved writable. The original UnauthorizedAccessException's
        // underlying ownership or integrity-level cause was not established.
        internal string ReceiptPrefix => _capturePrefix;

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
                .Select(character => invalid.Contains(character) || char.IsWhiteSpace(character) ? '-' : character)
                .ToArray());
            return string.IsNullOrWhiteSpace(sanitized) ? "unknown" : sanitized;
        }
    }
}
