using System.ComponentModel;
using System.Diagnostics;
using System.Text.RegularExpressions;

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
    string LaunchDiagnosticPath);

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
        Func<ProcessStartInfo, Process?> startProcess)
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
                    HostDiagnosticPath = sample.HostDiagnosticPath
                };
                DispatchProcessHost.WriteParameters(sample.ParametersPath, parameters);
                var startInfo = CreateStartInfo(parameters.WorkingDirectory, dispatchHostAssembly, sample.ParametersPath, sample.StartGatePath);
                ProcessSpawnGuard.ClearInheritableStateDatabaseHandles();
                var process = startProcess(startInfo);
                if (process is null)
                {
                    WriteLaunchDiagnostic(sample, "Process launcher returned no process.");
                    continue;
                }

                if (!WorkerProcessJobs.TryRegister(process, $"{jobPrefix}:planner-sample-{sample.Index}", out var failure))
                {
                    TryTerminate(process);
                    WriteLaunchDiagnostic(sample, failure);
                    continue;
                }

                launches.Add(new PlannerSampleLaunch(process, sample));
            }
            catch (Exception ex) when (IsSampleLaunchFailure(ex))
            {
                WriteLaunchDiagnostic(sample, ex.Message);
            }
        }

        return launches;
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
        int sampleCount)
    {
        var candidates = new List<PlannerCandidateInput>(sampleCount)
        {
            new(0, PlannerOutputContract.ReadCapturedOutputTail(primaryStandardOutputPath))
        };
        foreach (var sample in CreateArtifacts(primaryStandardOutputPath, sampleCount))
        {
            if (!DispatchExitArtifacts.TryRead(sample.ExitCodePath, out var exit) || exit.ExitCode != 0)
            {
                candidates.Add(new PlannerCandidateInput(
                    sample.Index,
                    string.Empty,
                    ReadLaunchDiagnostic(sample) ?? "Planner sample did not produce a successful exit artifact."));
                continue;
            }

            candidates.Add(new PlannerCandidateInput(
                sample.Index,
                PlannerOutputContract.ReadCapturedOutputTail(sample.StandardOutputPath),
                ReadBounded(sample.StandardErrorPath)));
        }

        return candidates;
    }

    internal static void ReleaseStartGates(IEnumerable<PlannerSampleLaunch> launches)
    {
        foreach (var launch in launches)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(launch.Artifacts.StartGatePath)!);
                File.WriteAllText(launch.Artifacts.StartGatePath, "go");
            }
            catch (Exception ex)
            {
                WriteLaunchDiagnostic(launch.Artifacts, $"Planner sample start gate could not be released: {ex.Message}");
                TerminateOwned(launch.Process);
                continue;
            }

            if (!WorkerProcessJobs.TryDetachForGracefulStop(launch.Process.Id, out var failure))
                WriteLaunchDiagnostic(launch.Artifacts, failure);
            launch.Process.Dispose();
        }
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
            stem + ".launch.err.log");
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
        try { File.WriteAllText(sample.LaunchDiagnosticPath, diagnostic); } catch { }
    }

    private static string? ReadLaunchDiagnostic(PlannerSampleArtifacts sample)
    {
        try { return File.Exists(sample.LaunchDiagnosticPath) ? ReadBounded(sample.LaunchDiagnosticPath) : null; }
        catch { return null; }
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
}
