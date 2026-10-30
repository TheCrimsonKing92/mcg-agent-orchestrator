using Mcg.AgentOrchestrator.Core;
using static Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier;

namespace Mcg.AgentOrchestrator.Infrastructure;

// Owns discovery, selection, normalized evidence and verdicts for one Crosswalk check.
// Process execution stays on the acceptance owner's existing heartbeat/drain seam.
internal sealed class CrosswalkAcceptanceCheck(
    IAcceptanceRunExecutionContext owner,
    AcceptanceInvocationContext invocation,
    TimeProvider timeProvider,
    CrosswalkExecutableResolver executableResolver,
    bool requiresTestTelemetryReceipt,
    Func<string[], GateHeartbeatContext> createHeartbeat,
    Func<string[], string, TimeSpan, GateHeartbeatContext, CancellationToken, CancellationToken, Task<CommandResult>> runProcess)
{
    // Reserve cmd.exe/pwsh quoting, arguments and heartbeat pipe redirects outside the script.
    private const int CommandWrapperMargin = 691;
    internal const int RunCommandLineLimit = MaxCmdCommandLineLength - CommandWrapperMargin;

    internal static Task<AcceptanceCheckResult> RunOwnedAsync(
        AcceptanceManifestCheck check, string worktreePath,
        IAcceptanceRunExecutionContext? owner, AcceptanceInvocationContext? invocation,
        TimeProvider timeProvider, CrosswalkExecutableResolver executableResolver,
        bool requiresTestTelemetryReceipt, Func<string[], GateHeartbeatContext> createHeartbeat,
        Func<string[], string, TimeSpan, GateHeartbeatContext, CancellationToken, CancellationToken, Task<CommandResult>> runProcess,
        CancellationToken cancellationToken)
        => new CrosswalkAcceptanceCheck(
            owner ?? throw new InvalidOperationException("Crosswalk requires an acceptance execution owner."),
            invocation ?? throw new InvalidOperationException("Crosswalk requires an owned invocation."),
            timeProvider, executableResolver, requiresTestTelemetryReceipt, createHeartbeat, runProcess)
            .RunAsync(check, worktreePath, cancellationToken);

    private async Task<AcceptanceCheckResult> RunAsync(
        AcceptanceManifestCheck check, string worktreePath, CancellationToken cancellationToken)
    {
        var timeout = owner.Settings.ResolveCheckTimeout(check.TimeoutMinutes);
        var started = timeProvider.GetTimestamp();
        using var deadline = new CancellationTokenSource(timeout, timeProvider);
        using var ownerLifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, owner.CancellationToken);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, owner.CancellationToken, deadline.Token);
        var diagnostics = new List<CrosswalkInvocationDiagnostic>();
        var stderr = new List<string>();
        var selected = Array.Empty<string>();
        var refusals = Array.Empty<string>();
        IReadOnlyList<TestResult> records = [];
        string[] paths = [];
        Mcg.AgentOrchestrator.Core.AcceptanceCohortCheckEvidence? cohortEvidence = null;
        int? exitCode = null;
        int? discovered = null;
        CommandResult? lastProcess = null;
        string? heartbeatPath = null;
        CrosswalkExecutableIdentity? executable = null;
        try
        {
            var root = ResolveCrosswalkProjectRoot(worktreePath, check.Project);
            var client = new CrosswalkClient(executableResolver, runProcessAsync: async (request, token) =>
            {
                // Revalidate links immediately before each launch. Execute the resolved directory,
                // never an unverified external artifact path.
                if (!string.Equals(root, ResolveCrosswalkProjectRoot(worktreePath, check.Project), CrosswalkPathComparison))
                    throw new IOException("Crosswalk project root changed before launch.");
                var arguments = new[] { WorkerShell.Executable }.Concat(WorkerShell.BaseArguments()).Append(request.Command).ToArray();
                var heartbeat = createHeartbeat(arguments);
                heartbeatPath = heartbeat.HeartbeatPath;
                lastProcess = await owner.TrackStarted(runProcess(arguments, root,
                    request.Timeout ?? Remaining(), heartbeat, requiresTestTelemetryReceipt ? ownerLifetime.Token : token,
                    lifetime.Token)).ConfigureAwait(false);
                // Production always supplies separated stdout. The injected runner contract must too.
                return new WorkerProcessRunResult(lastProcess.ExitCode, lastProcess.StandardOutput ?? "",
                    lastProcess.Stderr ?? "", lastProcess.TimedOut, lastProcess.CaptureLimited);
            });

            var list = await client.ListAsync(root, Remaining(), lifetime.Token).ConfigureAwait(false);
            executable = list.Executable;
            AddDiagnostics(list.Diagnostics);
            stderr.Add(list.StandardError);
            exitCode = list.ExitCode;
            if (diagnostics.Count > 0 || Expired()) return Finish();
            var ids = list.Result.Select(test => test.Id).ToArray();
            discovered = ids.Length;
            var focusedSelection = CrosswalkFocusedEvidence.IsEvidenceCheck(check) && check.Arguments.Count > 0;
            if (ids.Length == 0 && !focusedSelection)
                diagnostics.Add(new(CrosswalkDiagnosticCategory.EmptyDiscovery, "Crosswalk discovered no tests."));
            else if (ids.Any(string.IsNullOrWhiteSpace) || ids.Distinct(StringComparer.Ordinal).Count() != ids.Length)
                diagnostics.Add(new(CrosswalkDiagnosticCategory.MalformedResults, "Discovery IDs must be nonblank and unique."));
            if (diagnostics.Count > 0) return Finish();

            if (focusedSelection)
            {
                var resolution = CrosswalkSelectorResolution.Resolve(check.Arguments, ids);
                selected = resolution.Selected;
                refusals = resolution.Refused.Select(selector => CrosswalkSelectorResolution.RefusalDetail(selector, resolution.DiscoveredFiles)).ToArray();
                diagnostics.AddRange(refusals.Select(detail => new CrosswalkInvocationDiagnostic(CrosswalkDiagnosticCategory.InvalidSelection, detail)));
                if (selected.Length == 0) return Finish();
            }
            else
                selected = check.Arguments.Count == 0 ? ids : check.Arguments.ToArray();
            var known = ids.ToHashSet(StringComparer.Ordinal);
            if (selected.Length == 0 || selected.Any(string.IsNullOrWhiteSpace) || selected.Distinct(StringComparer.Ordinal).Count() != selected.Length ||
                selected.Any(id => !known.Contains(id)))
            {
                diagnostics.Add(new(CrosswalkDiagnosticCategory.InvalidSelection, "Selectors must be nonblank, unique, exact discovered IDs."));
                return Finish();
            }
            if (Expired()) return Finish();
            var run = check.Arguments.Count == 0
                ? await client.RunAsync(root, [], Remaining(), lifetime.Token).ConfigureAwait(false)
                : await new CrosswalkBatchedRunner(client, RunCommandLineLimit)
                    .RunAsync(root, list.Executable!, selected, Remaining, lifetime.Token).ConfigureAwait(false);
            AddDiagnostics(run.Diagnostics);
            stderr.Add(run.StandardError);
            exitCode = run.ExitCode;
            if (run.Result.Select(record => record.Id).Distinct(StringComparer.Ordinal).Count() != run.Result.Count)
            {
                diagnostics.Add(new(CrosswalkDiagnosticCategory.MalformedResults, "Result IDs must be unique across batches."));
                return Finish();
            }

            // A failed invocation can still return valuable evidence. Only current captured records
            // are published; neither old files nor paths embedded in stdout are evidence inputs.
            if (run.ExitCode is not null && run.Diagnostics.All(d => d.Category is not (CrosswalkDiagnosticCategory.InvalidJson or CrosswalkDiagnosticCategory.MalformedResults)))
            {
                var prefix = owner.ResultsPrefix;
                var directory = Path.GetDirectoryName(prefix)!;
                Directory.CreateDirectory(directory);
                AcceptanceAttemptArtifactCustody.ThrowIfLiveCustodianBlocksTakeover(directory, owner.RunId);
                if (owner.ArtifactCustody is { } custody)
                    AcceptanceAttemptArtifactCustody.Write(directory, custody.AttemptId, custody.LivenessCheckHint, custody.OwnerProcessId);
                var stem = $"{Path.GetFileName(prefix)}.{AcceptanceArtifactFileNames.Slug(check.Name)}-{ShortHash(check.Name)}-run-{invocation.Ordinal}-{Guid.NewGuid():N}";
                var path = Path.Combine(directory, AcceptanceArtifactFileNames.BoundFileName(stem, NormalizedTestResultWriter.FileSuffix));
                NormalizedTestResultWriter.Write(path, "crosswalk", run.Result);
                var persisted = new NormalizedTestResultSource().Read(path);
                if (persisted.Status != TestResultReadStatus.Readable)
                    throw new IOException("Crosswalk normalized receipt could not be read back.");
                records = persisted.Records;
                paths = [path];
                cohortEvidence = new(check.Name, Mcg.AgentOrchestrator.Core.AcceptanceCohortCheckEvidence.NormalizedFormat,
                    check.Project ?? "", owner.RunId, invocation.Ordinal, exitCode, directory, selected,
                    records.Count(record => record.Status != TestResultStatus.Skipped),
                    [CohortTestResultEvidence.Capture(path, Mcg.AgentOrchestrator.Core.AcceptanceCohortCheckEvidence.NormalizedFormat)]);
                if (records.Any(record => string.IsNullOrWhiteSpace(record.Id)))
                    diagnostics.Add(new(CrosswalkDiagnosticCategory.MalformedResults, "Normalized IDs must be nonblank."));
                if (records.Any(record => !known.Contains(record.Id)))
                    diagnostics.Add(new(CrosswalkDiagnosticCategory.MalformedResults, "Result IDs must be exact discovered IDs."));
                var executed = records.Where(record => record.Status is TestResultStatus.Passed or TestResultStatus.Failed or TestResultStatus.Error)
                    .Select(record => record.Id).ToHashSet(StringComparer.Ordinal);
                if (executed.Count == 0)
                    diagnostics.Add(new(CrosswalkDiagnosticCategory.NoExecution, "Crosswalk reported no executed tests."));
                var missing = selected.Where(id => !executed.Contains(id)).ToArray();
                if (missing.Length > 0)
                    diagnostics.Add(new(CrosswalkDiagnosticCategory.MissingCoverage,
                        $"Not every selected ID has an executed result. Missing {missing.Length}: {string.Join(", ", missing.Take(20))}" +
                        (missing.Length > 20 ? $"; and {missing.Length - 20} more." : ".")));
                if (records.Any(record => record.Status is TestResultStatus.Failed or TestResultStatus.Error))
                    diagnostics.Add(new(CrosswalkDiagnosticCategory.TestFailures, "Crosswalk reported failed tests."));
            }
        }
        catch (OperationCanceledException)
        {
            Expired();
            if (diagnostics.Count == 0) diagnostics.Add(new(CrosswalkDiagnosticCategory.Cancelled, "Crosswalk invocation cancelled."));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            diagnostics.Add(new(discovered is null ? CrosswalkDiagnosticCategory.InvalidProject : CrosswalkDiagnosticCategory.ReceiptFailure, ex.Message));
        }
        return Finish();

        TimeSpan Remaining() => timeout - timeProvider.GetElapsedTime(started);
        bool Expired()
        {
            var category = cancellationToken.IsCancellationRequested || owner.CancellationToken.IsCancellationRequested
                ? CrosswalkDiagnosticCategory.Cancelled
                : deadline.IsCancellationRequested || Remaining() <= TimeSpan.Zero ? CrosswalkDiagnosticCategory.Timeout : (CrosswalkDiagnosticCategory?)null;
            if (category is null) return false;
            if (!diagnostics.Any(d => d.Category == category)) diagnostics.Insert(0, new(category.Value, "Crosswalk check deadline or owner cancellation reached."));
            return true;
        }
        void AddDiagnostics(IReadOnlyList<CrosswalkInvocationDiagnostic> values)
        {
            diagnostics.AddRange(values.Select(value => value.Category == CrosswalkDiagnosticCategory.Cancelled && deadline.IsCancellationRequested &&
                !cancellationToken.IsCancellationRequested && !owner.CancellationToken.IsCancellationRequested
                    ? value with { Category = CrosswalkDiagnosticCategory.Timeout } : value));
        }
        AcceptanceCheckResult Finish()
        {
            Expired(); // Publication/validation also consumes the one check budget.
            var passed = diagnostics.Count == 0 && paths.Length > 0;
            return new AcceptanceCheckResult(check.Name, passed, exitCode,
                passed ? null : string.Join("; ", diagnostics.Select(d => $"{d.Category}: {d.Detail}")),
                ArtifactsPath: paths.FirstOrDefault() is { } path ? Path.GetDirectoryName(path) : null,
                DurationMilliseconds: (long)timeProvider.GetElapsedTime(started).TotalMilliseconds,
                ResultSummary: $"crosswalk: selected={selected.Length} results={records.Count}" +
                    (refusals.Length == 0 ? "" : "; " + string.Join("; ", refusals)), Advisory: check.Advisory,
                TestResultAttemptId: owner.RunId, TestResultRunOrdinal: invocation.Ordinal,
                FailureClassification: passed ? null : $"crosswalk-{diagnostics.FirstOrDefault()?.Category}",
                FailingTestIdentities: records.Where(r => r.Status is TestResultStatus.Failed or TestResultStatus.Error).Select(r => r.Id).ToArray(),
                ExecutedTestCount: records.Count(r => r.Status is TestResultStatus.Passed or TestResultStatus.Failed or TestResultStatus.Error),
                DiscoveredTestCount: discovered, ProcessStderr: string.Concat(stderr), ProcessStderrPath: lastProcess?.StderrPath,
                GateHeartbeatPath: heartbeatPath, ChildProcessId: lastProcess?.ChildProcessId, ChildProcessStartedAt: lastProcess?.ChildProcessStartedAt,
                SkippedTestIdentities: records.Where(r => r.Status == TestResultStatus.Skipped).Select(r => r.Id).ToArray())
            {
                NormalizedTestResultPaths = paths, CrosswalkDiagnostics = diagnostics.ToArray(), CohortEvidence = cohortEvidence,
                CrosswalkExecutablePath = executable?.Path, CrosswalkExecutableSha256 = executable?.Sha256
            };
        }
    }

    private static StringComparison CrosswalkPathComparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static string ResolveCrosswalkProjectRoot(string worktreePath, string? project)
    {
        if (!string.IsNullOrEmpty(project) && (Path.IsPathRooted(project) || project.Contains(':')))
            throw new IOException("Crosswalk Project must be repository-relative.");
        var lexicalRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(worktreePath));
        var target = Path.GetFullPath(string.IsNullOrEmpty(project) ? "." : project, lexicalRoot);
        if (!Contained(lexicalRoot, target)) throw new IOException("Crosswalk Project escapes the candidate worktree.");
        var physicalRoot = ResolveDirectory(lexicalRoot);
        var current = physicalRoot;
        var relative = Path.GetRelativePath(lexicalRoot, target);
        foreach (var component in relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            if (component == ".") continue;
            current = ResolveDirectory(Path.Combine(current, component));
            if (!Contained(physicalRoot, current)) throw new IOException("Crosswalk Project link escapes the candidate worktree.");
        }
        return current;

        static bool Contained(string root, string value) => value.Equals(root, CrosswalkPathComparison) ||
            value.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, CrosswalkPathComparison);
        static string ResolveDirectory(string path, int linkDepth = 0)
        {
            if (linkDepth > 40) throw new IOException("Crosswalk Project has an unresolved link cycle.");
            var full = Path.GetFullPath(path);
            var current = Path.GetPathRoot(full)!;
            foreach (var component in full[current.Length..].Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
            {
                var directory = new DirectoryInfo(Path.Combine(current, component));
                if (!directory.Exists) throw new IOException("Crosswalk Project directory does not exist or cannot be resolved.");
                if (directory.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    var target = directory.ResolveLinkTarget(true)?.FullName
                        ?? throw new IOException("Crosswalk Project link cannot be resolved.");
                    // Resolve ancestor links in the target too; ResolveLinkTarget alone only
                    // follows the terminal link chain, not every component in its returned path.
                    current = ResolveDirectory(target, linkDepth + 1);
                }
                else current = directory.FullName;
                if (!Directory.Exists(current)) throw new IOException("Crosswalk Project is not a directory.");
            }
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(current));
        }
    }
}
