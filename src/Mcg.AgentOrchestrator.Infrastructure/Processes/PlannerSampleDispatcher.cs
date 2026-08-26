using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal sealed record PlannerSampleArtifacts(
    int Index,
    string ParametersPath,
    string StandardOutputPath,
    string StandardErrorPath,
    string ExitCodePath,
    string HeartbeatPath,
    string ChildExitRecordPath,
    string HostDiagnosticPath,
    string StartGatePath,
    string? PrepRecordPath,
    string? PrepHeartbeatPath,
    string? PrepExitCodePath,
    string LaunchRecordPath,
    string LaunchDiagnosticPath,
    string TerminalRecordPath);

internal sealed record PlannerSampleLaunch(Process Process, PlannerSampleArtifacts Artifacts);

internal static class PlannerSampleDispatcher
{
    internal static IReadOnlyList<PlannerSampleArtifacts> CreateArtifacts(string primaryStandardOutputPath, int sampleCount)
    {
        if (sampleCount <= 1)
            return [];

        const string suffix = ".out.log";
        var prefix = primaryStandardOutputPath.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
            ? primaryStandardOutputPath[..^suffix.Length]
            : primaryStandardOutputPath;
        return Enumerable.Range(1, sampleCount - 1)
            .Select(index => CreateArtifactSet(prefix, index))
            .ToArray();
    }

    internal static IReadOnlyList<PlannerSampleLaunch> StartSamples(
        IReadOnlyList<PlannerSampleArtifacts> artifacts,
        DispatchProcessHost.DispatchRunParameters primaryParameters,
        string dispatchHostAssembly,
        string jobPrefix,
        Func<ProcessStartInfo, Process?> startProcess,
        Func<DateTimeOffset>? utcNow = null)
    {
        var launches = new List<PlannerSampleLaunch>();
        foreach (var sample in artifacts)
        {
            try
            {
                var parameters = primaryParameters with
                {
                    Command = BuildIndependentCommand(primaryParameters.Command, primaryParameters.Provider),
                    StdoutPath = sample.StandardOutputPath,
                    StderrPath = sample.StandardErrorPath,
                    ExitCodePath = sample.ExitCodePath,
                    HeartbeatPath = sample.HeartbeatPath,
                    ProviderSessionId = null,
                    PrepTaskId = primaryParameters.PrepTaskId is null
                        ? null
                        : $"{primaryParameters.PrepTaskId}-sample-{sample.Index}",
                    PrepRecordPath = primaryParameters.PrepRecordPath is null ? null : sample.PrepRecordPath,
                    PrepHeartbeatPath = primaryParameters.PrepHeartbeatPath is null ? null : sample.PrepHeartbeatPath,
                    PrepExitCodePath = primaryParameters.PrepExitCodePath is null ? null : sample.PrepExitCodePath,
                    ChildExitRecordPath = sample.ChildExitRecordPath,
                    HostDiagnosticPath = sample.HostDiagnosticPath,
                    SandboxInstanceName = $"planner-sample-{sample.Index}"
                };
                DispatchProcessHost.WriteParameters(sample.ParametersPath, parameters);
                var startInfo = CreateStartInfo(parameters.WorkingDirectory, dispatchHostAssembly, sample.ParametersPath, sample.StartGatePath);
                ProcessSpawnGuard.ClearInheritableStateDatabaseHandles();
                var process = startProcess(startInfo);
                if (process is null)
                {
                    WriteLaunchDiagnosticOrTerminateAdmitted(
                        sample,
                        "Process launcher returned no process.",
                        launches);
                    continue;
                }

                if (!WorkerProcessJobs.TryRegister(process, $"{jobPrefix}:planner-sample-{sample.Index}", out var failure))
                {
                    TryTerminate(process);
                    WriteLaunchDiagnosticOrTerminateAdmitted(sample, failure, launches);
                    continue;
                }

                if (!TryWriteLaunchRecord(
                        sample,
                        new PlannerSampleLaunchRecord(process.Id, (utcNow ?? (() => DateTimeOffset.UtcNow))()),
                        out var launchRecordDiagnostic))
                {
                    TerminateOwned(process);
                    WriteLaunchDiagnosticOrTerminateAdmitted(sample, launchRecordDiagnostic, launches);
                    continue;
                }

                launches.Add(new PlannerSampleLaunch(process, sample));
            }
            catch (Exception ex) when (IsSampleLaunchFailure(ex))
            {
                WriteLaunchDiagnosticOrTerminateAdmitted(sample, ex.Message, launches);
            }
        }

        return launches;
    }

    private static void WriteLaunchDiagnosticOrTerminateAdmitted(
        PlannerSampleArtifacts sample,
        string diagnostic,
        IReadOnlyList<PlannerSampleLaunch> admittedLaunches)
    {
        try
        {
            WriteLaunchDiagnostic(sample, diagnostic);
        }
        catch
        {
            TerminateUnreleased(admittedLaunches);
            throw;
        }
    }

    // Every documented Process.Start(ProcessStartInfo) failure degrades optional sampling. The
    // inherited cases are deliberate: IOException includes FileNotFoundException, while
    // InvalidOperationException includes ObjectDisposedException. ArgumentNullException cannot
    // occur because CreateStartInfo supplies the instance; any other exception is a code defect.
    private static bool IsSampleLaunchFailure(Exception exception) => exception is
        Win32Exception or
        IOException or
        UnauthorizedAccessException or
        InvalidOperationException or
        PlatformNotSupportedException;

    internal static IReadOnlyList<PlannerCandidateInput> CollectCandidates(
        string primaryStandardOutputPath,
        int sampleCount,
        TaskDispatchRecord? dispatch = null,
        long? primaryElapsedMilliseconds = null)
    {
        var primaryArtifactHash = HashArtifact(primaryStandardOutputPath);
        var primaryNormalization = StructuredCodexOutputNormalizer.Normalize(dispatch, primaryStandardOutputPath);
        var candidates = new List<PlannerCandidateInput>(sampleCount)
        {
            new(
                0,
                primaryNormalization.State == PlannerCandidateNormalizationState.Normalized
                    ? primaryNormalization.Parsed!.WorkerOutput
                    : PlannerOutputContract.ReadCapturedOutputTail(primaryStandardOutputPath),
                SourcePath: primaryStandardOutputPath,
                ArtifactSha256: primaryArtifactHash,
                NormalizationState: primaryNormalization.State,
                ElapsedMilliseconds: primaryElapsedMilliseconds,
                ProviderUsage: primaryNormalization.Parsed?.Usage,
                ProviderUsageUnavailableReason: primaryNormalization.Parsed?.UsageUnavailableReason ?? "unsupported")
        };
        foreach (var sample in CreateArtifacts(primaryStandardOutputPath, sampleCount))
        {
            var launchDiagnostic = ReadLaunchDiagnostic(sample);
            var artifactHash = HashArtifact(sample.StandardOutputPath);
            var normalization = StructuredCodexOutputNormalizer.Normalize(dispatch, sample.StandardOutputPath);
            if (!DispatchExitArtifacts.TryRead(sample.ExitCodePath, out var exit))
            {
                var terminal = ClassifyMissingExit(sample, launchDiagnostic);
                candidates.Add(new PlannerCandidateInput(
                    sample.Index,
                    string.Empty,
                    launchDiagnostic ?? "Planner sample did not produce an exit artifact.",
                    sample.StandardOutputPath,
                    ArtifactSha256: artifactHash,
                    TerminalState: terminal.State,
                    NormalizationState: normalization.State,
                    ElapsedMilliseconds: ResolveElapsedMilliseconds(sample, terminal.RecordedAt),
                    ProviderUsage: normalization.Parsed?.Usage,
                    ProviderUsageUnavailableReason: normalization.Parsed?.UsageUnavailableReason ?? "unsupported"));
                continue;
            }

            if (exit.ExitCode != 0)
            {
                var terminal = ReadTerminalRecord(sample);
                candidates.Add(new PlannerCandidateInput(
                    sample.Index,
                    string.Empty,
                    ReadBounded(sample.StandardErrorPath),
                    sample.StandardOutputPath,
                    ArtifactSha256: artifactHash,
                    TerminalState: terminal.ReadState switch
                    {
                        PlannerSampleTerminalReadState.Read => terminal.Record!.State,
                        PlannerSampleTerminalReadState.Unreadable => PlannerCandidateTerminalState.UnreadableTerminalArtifact,
                        PlannerSampleTerminalReadState.Malformed => PlannerCandidateTerminalState.MalformedTerminalArtifact,
                        _ => PlannerCandidateTerminalState.NonZeroExit
                    },
                    NormalizationState: normalization.State,
                    ElapsedMilliseconds: ResolveElapsedMilliseconds(sample, exit.RecordedAt),
                    ProviderUsage: normalization.Parsed?.Usage,
                    ProviderUsageUnavailableReason: normalization.Parsed?.UsageUnavailableReason ?? "unsupported"));
                continue;
            }

            candidates.Add(new PlannerCandidateInput(
                sample.Index,
                normalization.State == PlannerCandidateNormalizationState.Normalized
                    ? normalization.Parsed!.WorkerOutput
                    : PlannerOutputContract.ReadCapturedOutputTail(sample.StandardOutputPath),
                ReadBounded(sample.StandardErrorPath),
                sample.StandardOutputPath,
                artifactHash,
                PlannerCandidateTerminalState.Succeeded,
                normalization.State,
                ResolveElapsedMilliseconds(sample, exit.RecordedAt),
                normalization.Parsed?.Usage,
                normalization.Parsed?.UsageUnavailableReason ?? "unsupported"));
        }

        return candidates;
    }

    internal static TimeSpan ResolveSampleWait(
        DateTimeOffset primaryStartedAt,
        DispatchExitArtifact primaryExit)
    {
        var primaryRuntime = primaryExit.RecordedAt - primaryStartedAt;
        return primaryRuntime > TimeSpan.FromMinutes(1)
            ? primaryRuntime
            : TimeSpan.FromMinutes(1);
    }

    internal static bool AnySampleUnresolved(string primaryStandardOutputPath, int sampleCount) =>
        CreateArtifacts(primaryStandardOutputPath, sampleCount).Any(sample =>
            !File.Exists(sample.LaunchDiagnosticPath) &&
            !DispatchExitArtifacts.TryRead(sample.ExitCodePath, out _));

    internal static void RecordTimedOutSamples(
        string primaryStandardOutputPath,
        int sampleCount,
        TimeSpan wait,
        DateTimeOffset deadline)
    {
        foreach (var sample in CreateArtifacts(primaryStandardOutputPath, sampleCount))
        {
            if (File.Exists(sample.LaunchDiagnosticPath) ||
                DispatchExitArtifacts.TryRead(sample.ExitCodePath, out _))
            {
                continue;
            }

            var diagnostic = $"Planner sample timed out after the bounded {wait:c} wait ended at {deadline:O}; " +
                             "the sample was terminated and excluded from candidate selection.";
            WriteTerminalRecord(
                sample,
                new PlannerSampleTerminalRecord(PlannerCandidateTerminalState.TimedOut, deadline));
            WriteLaunchDiagnostic(sample, diagnostic);
        }
    }

    internal static void ReleaseStartGates(
        IEnumerable<PlannerSampleLaunch> launches,
        Action<Process>? terminateOwned = null,
        Action<PlannerSampleArtifacts, string>? writeLaunchDiagnostic = null)
    {
        terminateOwned ??= TerminateOwned;
        writeLaunchDiagnostic ??= WriteLaunchDiagnostic;
        Exception? deferredDiagnosticFailure = null;
        foreach (var launch in launches)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(launch.Artifacts.StartGatePath)!);
                File.WriteAllText(launch.Artifacts.StartGatePath, "go");
            }
            catch (Exception ex)
            {
                terminateOwned(launch.Process);
                try
                {
                    writeLaunchDiagnostic(
                        launch.Artifacts,
                        $"Planner sample start gate could not be released: {ex.Message}");
                }
                catch (Exception diagnosticFailure)
                {
                    deferredDiagnosticFailure ??= diagnosticFailure;
                }
                continue;
            }

            launch.Process.Dispose();
        }

        if (deferredDiagnosticFailure is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(deferredDiagnosticFailure).Throw();
    }

    internal static void TerminateUnreleased(IEnumerable<PlannerSampleLaunch> launches)
    {
        foreach (var launch in launches)
            TerminateOwned(launch.Process);
    }

    private static PlannerSampleArtifacts CreateArtifactSet(string prefix, int index)
    {
        var stem = $"{prefix}.sample-{index}";
        return new PlannerSampleArtifacts(
            index,
            stem + ".dispatch.json",
            stem + ".out.log",
            stem + ".err.log",
            stem + ".exit.txt",
            stem + ".heartbeat.json",
            stem + ".child-exit.json",
            stem + ".host.err.log",
            stem + ".start-gate",
            stem + ".prep.json",
            stem + ".prep.heartbeat.json",
            stem + ".prep.exit.txt",
            stem + ".launch.json",
            stem + ".launch.err.log",
            stem + ".terminal.json");
    }

    private static ProcessStartInfo CreateStartInfo(
        string workingDirectory,
        string dispatchHostAssembly,
        string parametersPath,
        string startGatePath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory
        };
        if (OperatingSystem.IsWindows())
            startInfo.CreateNewProcessGroup = true;
        startInfo.Environment[DispatchProcessHost.StartGatePathVariable] = startGatePath;
        startInfo.ArgumentList.Add("exec");
        startInfo.ArgumentList.Add(dispatchHostAssembly);
        startInfo.ArgumentList.Add(DispatchProcessHost.SubcommandName);
        startInfo.ArgumentList.Add(parametersPath);
        return startInfo;
    }

    internal static string BuildIndependentCommand(string command, WorkerSandboxProvider provider)
    {
        if (provider != WorkerSandboxProvider.Claude)
            return command;

        var sessionId = Guid.NewGuid().ToString();
        var pattern = @"(?<!\S)--session-id\s+(?:""[^""]+""|'[^']+'|\S+)";
        return Regex.IsMatch(command, pattern, RegexOptions.CultureInvariant)
            ? Regex.Replace(command, pattern, $"--session-id {sessionId}", RegexOptions.CultureInvariant)
            : $"{command} --session-id {sessionId}";
    }

    private static void TryTerminate(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            process.Dispose();
        }
        catch
        {
            // Launch failure remains the actionable diagnostic.
        }
    }

    private static void TerminateOwned(Process process)
    {
        try { WorkerProcessJobs.TryKillOrFallback(process.Id); }
        catch { /* Cleanup is best-effort after the actionable launch/checkpoint failure. */ }
        finally { process.Dispose(); }
    }

    private static void WriteLaunchDiagnostic(PlannerSampleArtifacts sample, string diagnostic)
    {
        File.WriteAllText(sample.LaunchDiagnosticPath, diagnostic);
    }

    private static string? ReadLaunchDiagnostic(PlannerSampleArtifacts sample)
    {
        try { return File.Exists(sample.LaunchDiagnosticPath) ? ReadBounded(sample.LaunchDiagnosticPath) : null; }
        catch { return null; }
    }

    private static PlannerSampleTerminalClassification ClassifyMissingExit(
        PlannerSampleArtifacts sample,
        string? diagnostic)
    {
        var terminal = ReadTerminalRecord(sample);
        return terminal.ReadState switch
        {
            PlannerSampleTerminalReadState.Read => new(terminal.Record!.State, terminal.Record.RecordedAt),
            PlannerSampleTerminalReadState.Unreadable => new(
                PlannerCandidateTerminalState.UnreadableTerminalArtifact,
                null),
            PlannerSampleTerminalReadState.Malformed => new(
                PlannerCandidateTerminalState.MalformedTerminalArtifact,
                null),
            _ => new(
                string.IsNullOrWhiteSpace(diagnostic)
                    ? PlannerCandidateTerminalState.MissingExitArtifact
                    : PlannerCandidateTerminalState.LaunchFailed,
                null)
        };
    }

    private static bool TryWriteLaunchRecord(
        PlannerSampleArtifacts sample,
        PlannerSampleLaunchRecord record,
        out string diagnostic)
    {
        diagnostic = string.Empty;
        try
        {
            File.WriteAllText(sample.LaunchRecordPath, JsonSerializer.Serialize(record));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            diagnostic = $"Planner sample launch record could not be persisted: {ex.Message}";
            return false;
        }
    }

    internal static void RecordCancelledSamples(
        string primaryStandardOutputPath,
        int sampleCount,
        DateTimeOffset cancelledAt)
    {
        foreach (var sample in CreateArtifacts(primaryStandardOutputPath, sampleCount))
        {
            if (File.Exists(sample.TerminalRecordPath) ||
                DispatchExitArtifacts.TryRead(sample.ExitCodePath, out _))
            {
                continue;
            }

            WriteTerminalRecord(
                sample,
                new PlannerSampleTerminalRecord(PlannerCandidateTerminalState.Cancelled, cancelledAt));
        }
    }

    private static void WriteTerminalRecord(
        PlannerSampleArtifacts sample,
        PlannerSampleTerminalRecord record) =>
        File.WriteAllText(sample.TerminalRecordPath, JsonSerializer.Serialize(record));

    private static PlannerSampleTerminalReadResult ReadTerminalRecord(PlannerSampleArtifacts sample)
    {
        if (!File.Exists(sample.TerminalRecordPath))
            return new(PlannerSampleTerminalReadState.Missing, null);
        try
        {
            var payload = JsonSerializer.Deserialize<PlannerSampleTerminalRecordPayload>(
                File.ReadAllText(sample.TerminalRecordPath));
            if (payload?.State is not { } state ||
                !Enum.IsDefined(state) ||
                payload.RecordedAt is not { } recordedAt ||
                recordedAt == default)
            {
                return new(PlannerSampleTerminalReadState.Malformed, null);
            }

            return new(
                PlannerSampleTerminalReadState.Read,
                new PlannerSampleTerminalRecord(state, recordedAt));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new(PlannerSampleTerminalReadState.Unreadable, null);
        }
        catch (JsonException)
        {
            return new(PlannerSampleTerminalReadState.Malformed, null);
        }
    }

    private static long? ResolveElapsedMilliseconds(PlannerSampleArtifacts sample, DateTimeOffset? completedAt)
    {
        if (completedAt is null)
            return null;
        try
        {
            var launch = JsonSerializer.Deserialize<PlannerSampleLaunchRecord>(File.ReadAllText(sample.LaunchRecordPath));
            if (launch is null || completedAt < launch.StartedAt)
                return null;
            return (long)(completedAt.Value - launch.StartedAt).TotalMilliseconds;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static string? HashArtifact(string standardOutputPath)
    {
        var rawAuditPath = standardOutputPath + ".jsonl";
        var path = File.Exists(rawAuditPath)
            ? rawAuditPath
            : File.Exists(standardOutputPath)
                ? standardOutputPath
                : null;
        if (path is null)
            return null;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string ReadBounded(string path)
    {
        try
        {
            if (!File.Exists(path))
                return string.Empty;
            var text = File.ReadAllText(path);
            return text.Length <= 8_192 ? text : text[^8_192..];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ex.Message;
        }
    }

    private sealed record PlannerSampleTerminalRecord(
        PlannerCandidateTerminalState State,
        DateTimeOffset RecordedAt);

    private sealed record PlannerSampleTerminalRecordPayload(
        PlannerCandidateTerminalState? State,
        DateTimeOffset? RecordedAt);

    private sealed record PlannerSampleTerminalClassification(
        PlannerCandidateTerminalState State,
        DateTimeOffset? RecordedAt);

    private sealed record PlannerSampleTerminalReadResult(
        PlannerSampleTerminalReadState ReadState,
        PlannerSampleTerminalRecord? Record);

    private enum PlannerSampleTerminalReadState
    {
        Missing,
        Read,
        Unreadable,
        Malformed
    }

    private sealed record PlannerSampleLaunchRecord(int ProcessId, DateTimeOffset StartedAt);
}
