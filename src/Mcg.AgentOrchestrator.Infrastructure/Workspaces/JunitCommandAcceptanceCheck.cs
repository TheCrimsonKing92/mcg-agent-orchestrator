using Mcg.AgentOrchestrator.Core;
using static Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier;

namespace Mcg.AgentOrchestrator.Infrastructure;

/// <summary>Owns one declared suite invocation, its fresh reports, and its merged gate receipt.</summary>
internal sealed class JunitCommandAcceptanceCheck(
    IAcceptanceRunExecutionContext owner,
    AcceptanceInvocationContext invocation,
    TimeProvider timeProvider,
    CrosswalkExecutableResolver executableResolver,
    bool requiresTestTelemetryReceipt,
    Func<string[], GateHeartbeatContext> createHeartbeat,
    Func<string[], string, TimeSpan, GateHeartbeatContext, CancellationToken, CancellationToken, Task<CommandResult>> runCommand,
    Func<string[], string, TimeSpan, GateHeartbeatContext, CancellationToken, CancellationToken, Task<CommandResult>> runParser)
{
    internal static Task<AcceptanceCheckResult> RunOwnedAsync(
        AcceptanceManifestCheck check, string worktreePath,
        IAcceptanceRunExecutionContext? owner, AcceptanceInvocationContext? invocation,
        TimeProvider timeProvider, CrosswalkExecutableResolver executableResolver, bool requiresTestTelemetryReceipt,
        Func<string[], GateHeartbeatContext> createHeartbeat,
        Func<string[], string, TimeSpan, GateHeartbeatContext, CancellationToken, CancellationToken, Task<CommandResult>> runCommand,
        Func<string[], string, TimeSpan, GateHeartbeatContext, CancellationToken, CancellationToken, Task<CommandResult>> runParser,
        CancellationToken cancellationToken) =>
        new JunitCommandAcceptanceCheck(
            owner ?? throw new InvalidOperationException("JUnit command requires an acceptance execution owner."),
            invocation ?? throw new InvalidOperationException("JUnit command requires an owned invocation."),
            timeProvider, executableResolver, requiresTestTelemetryReceipt, createHeartbeat, runCommand, runParser)
            .RunAsync(check, worktreePath, cancellationToken);

    private async Task<AcceptanceCheckResult> RunAsync(AcceptanceManifestCheck check, string worktreePath, CancellationToken cancellationToken)
    {
        var timeout = owner.Settings.ResolveCheckTimeout(check.TimeoutMinutes);
        var started = timeProvider.GetTimestamp();
        using var deadline = new CancellationTokenSource(timeout, timeProvider);
        using var ownerLifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, owner.CancellationToken);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ownerLifetime.Token, deadline.Token);
        var diagnostics = new List<CrosswalkInvocationDiagnostic>();
        var stderr = new List<string>();
        var records = new List<TestResult>();
        string[] paths = [];
        AcceptanceCohortCheckEvidence? cohortEvidence = null;
        CrosswalkExecutableIdentity? executable = null;
        CommandResult? lastProcess = null;
        string? heartbeatPath = null;
        int? exitCode = null;
        try
        {
            JunitCommandCheckDefinition.Validate(check);
            var root = CrosswalkAcceptanceCheck.ResolveCrosswalkProjectRoot(worktreePath, check.Project);
            var arguments = new[] { check.Command! }.Concat(check.Arguments).ToArray();
            DateTimeOffset reportStart = default;
            var command = await Launch(arguments, runCommand, startsReports: true).ConfigureAwait(false);
            exitCode = command.ExitCode;
            if (command.ExitCode != 0)
                diagnostics.Add(new(CrosswalkDiagnosticCategory.NonzeroExit, $"Declared command exited with exit code {command.ExitCode}."));
            if (command.TimedOut)
                diagnostics.Add(new(CrosswalkDiagnosticCategory.Timeout, "Declared command timed out."));
            if (command.CaptureLimited)
                diagnostics.Add(new(CrosswalkDiagnosticCategory.CaptureLimited, "Declared command output capture limit reached."));
            if (command.TimedOut || Expired()) return Finish();

            var reports = JunitReportGlob.FreshFiles(root, check.Reports, reportStart, lifetime.Token);
            if (reports.Count == 0)
                diagnostics.Add(new(CrosswalkDiagnosticCategory.NoExecution, $"No fresh JUnit reports match globs: {string.Join(", ", check.Reports)}."));
            else
            {
                var resolution = executableResolver.Resolve();
                executable = resolution.Identity;
                if (executable is null)
                    diagnostics.Add(new(resolution.DiagnosticCategory ?? CrosswalkDiagnosticCategory.MissingBinary,
                        $"JUnit parser unavailable: {resolution.ConfigurationDetail ?? resolution.Detail}"));
                else foreach (var report in reports)
                {
                    if (Expired()) break;
                    try
                    {
                        JunitReportGlob.EnsureContainedFile(root, report);
                        var parsed = await CrosswalkJunitParser.ParseAsync(executable, report, check.Name,
                            args => Launch(args, runParser)).ConfigureAwait(false);
                        foreach (var row in parsed.Records)
                            records.Add(JunitRecordPathRewriter.Rewrite(row, worktreePath, root));
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
                    {
                        diagnostics.Add(new(CrosswalkDiagnosticCategory.MalformedResults, $"JUnit report '{report}': {exception.Message}"));
                    }
                }
            }

            var duplicates = records.GroupBy(record => record.Id, StringComparer.Ordinal).Where(group => group.Count() > 1)
                .Select(group => group.Key).ToArray();
            if (duplicates.Length > 0)
                diagnostics.Add(new(CrosswalkDiagnosticCategory.MalformedResults, $"Duplicate JUnit test IDs: {string.Join(", ", duplicates)}."));
            if (records.Count == 0)
                diagnostics.Add(new(CrosswalkDiagnosticCategory.NoExecution, "JUnit reports contain no test rows."));
            var failures = records.Where(record => record.Status is TestResultStatus.Failed or TestResultStatus.Error).Select(record => record.Id).ToArray();
            if (failures.Length > 0)
                diagnostics.Add(new(CrosswalkDiagnosticCategory.TestFailures, $"JUnit failed or error rows: {string.Join(", ", failures)}."));

            if (records.Count > 0 && duplicates.Length == 0 && !Expired())
            {
                var prefix = owner.ResultsPrefix;
                var directory = Path.GetDirectoryName(prefix)!;
                Directory.CreateDirectory(directory);
                AcceptanceAttemptArtifactCustody.ThrowIfLiveCustodianBlocksTakeover(directory, owner.RunId);
                if (owner.ArtifactCustody is { } custody)
                    AcceptanceAttemptArtifactCustody.Write(directory, custody.AttemptId, custody.LivenessCheckHint, custody.OwnerProcessId);
                var stem = $"{Path.GetFileName(prefix)}.{AcceptanceArtifactFileNames.Slug(check.Name)}-{ShortHash(check.Name)}-run-{invocation.Ordinal}-{Guid.NewGuid():N}";
                var path = Path.Combine(directory, AcceptanceArtifactFileNames.BoundFileName(stem, NormalizedTestResultWriter.FileSuffix));
                NormalizedTestResultWriter.Write(path, check.Name, records);
                var persisted = new NormalizedTestResultSource().Read(path);
                if (persisted.Status != TestResultReadStatus.Readable) throw new IOException("JUnit normalized receipt could not be read back.");
                paths = [path];
                cohortEvidence = new(check.Name, AcceptanceCohortCheckEvidence.NormalizedFormat, check.Project ?? ".",
                    owner.RunId, invocation.Ordinal, exitCode, directory, records.Select(record => record.Id).ToArray(),
                    records.Count(record => record.Status != TestResultStatus.Skipped),
                    [CohortTestResultEvidence.Capture(path, AcceptanceCohortCheckEvidence.NormalizedFormat)]);
            }

            async Task<CommandResult> Launch(string[] args,
                Func<string[], string, TimeSpan, GateHeartbeatContext, CancellationToken, CancellationToken, Task<CommandResult>> process,
                bool startsReports = false)
            {
                lifetime.Token.ThrowIfCancellationRequested();
                if (root != CrosswalkAcceptanceCheck.ResolveCrosswalkProjectRoot(worktreePath, check.Project))
                    throw new IOException("JUnit project root changed before launch.");
                var heartbeat = createHeartbeat(args);
                heartbeatPath = heartbeat.HeartbeatPath;
                // The command and parser share one deadline, but only the command starts report freshness.
                if (startsReports) reportStart = timeProvider.GetUtcNow();
                lastProcess = await owner.TrackStarted(process(args, root, Remaining(), heartbeat,
                    requiresTestTelemetryReceipt ? ownerLifetime.Token : lifetime.Token, lifetime.Token)).ConfigureAwait(false);
                stderr.Add(lastProcess.Stderr ?? "");
                return lastProcess;
            }
        }
        catch (OperationCanceledException)
        {
            if (!Expired()) diagnostics.Add(new(CrosswalkDiagnosticCategory.Cancelled, "JUnit invocation cancelled."));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.ComponentModel.Win32Exception)
        {
            diagnostics.Add(new(exitCode is null ? CrosswalkDiagnosticCategory.StartFailed : CrosswalkDiagnosticCategory.ReceiptFailure, exception.Message));
        }
        return Finish();

        TimeSpan Remaining() => timeout - timeProvider.GetElapsedTime(started);
        bool Expired()
        {
            var category = ownerLifetime.IsCancellationRequested ? CrosswalkDiagnosticCategory.Cancelled
                : deadline.IsCancellationRequested || Remaining() <= TimeSpan.Zero ? CrosswalkDiagnosticCategory.Timeout : (CrosswalkDiagnosticCategory?)null;
            if (category is null) return false;
            if (!diagnostics.Any(item => item.Category == category)) diagnostics.Insert(0, new(category.Value, "JUnit check deadline or owner cancellation reached."));
            return true;
        }
        AcceptanceCheckResult Finish()
        {
            Expired();
            var passed = diagnostics.Count == 0 && paths.Length > 0;
            return new(check.Name, passed, exitCode, passed ? null : string.Join("; ", diagnostics.Select(item => $"{item.Category}: {item.Detail}")),
                ArtifactsPath: paths.FirstOrDefault() is { } path ? Path.GetDirectoryName(path) : null,
                DurationMilliseconds: (long)timeProvider.GetElapsedTime(started).TotalMilliseconds,
                ResultSummary: $"junit-command: results={records.Count}", Advisory: check.Advisory,
                TestResultAttemptId: owner.RunId, TestResultRunOrdinal: invocation.Ordinal,
                FailureClassification: passed ? null : $"junit-command-{diagnostics.FirstOrDefault()?.Category}",
                FailingTestIdentities: records.Where(record => record.Status is TestResultStatus.Failed or TestResultStatus.Error).Select(record => record.Id).ToArray(),
                ExecutedTestCount: records.Count(record => record.Status is TestResultStatus.Passed or TestResultStatus.Failed or TestResultStatus.Error),
                DiscoveredTestCount: records.Count, ProcessStderr: string.Concat(stderr), ProcessStderrPath: lastProcess?.StderrPath,
                GateHeartbeatPath: heartbeatPath, ChildProcessId: lastProcess?.ChildProcessId, ChildProcessStartedAt: lastProcess?.ChildProcessStartedAt,
                SkippedTestIdentities: records.Where(record => record.Status == TestResultStatus.Skipped).Select(record => record.Id).ToArray())
            {
                NormalizedTestResultPaths = paths, CrosswalkDiagnostics = diagnostics.ToArray(), CohortEvidence = cohortEvidence,
                CrosswalkExecutablePath = executable?.Path, CrosswalkExecutableSha256 = executable?.Sha256
            };
        }
    }
}
