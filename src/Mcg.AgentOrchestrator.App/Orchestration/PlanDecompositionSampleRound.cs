using System.Text;
using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal abstract record PlanDecompositionSampleResult
{
    internal sealed record Succeeded(string StandardOutput) : PlanDecompositionSampleResult;
    // A null exit code means dispatch failed before a process exit was available.
    internal sealed record Failed(int? ExitCode, string StandardErrorTail, string RawOutput)
        : PlanDecompositionSampleResult;
}

internal static class PlanDecompositionSampleRound
{
    private static readonly AsyncLocal<Func<WorkerProcessRunRequest, CancellationToken,
        Task<WorkerProcessRunResult>>?> RunnerOverride = new();

    internal static Func<WorkerProcessRunRequest, CancellationToken, Task<WorkerProcessRunResult>> ProcessRunner =>
        RunnerOverride.Value ?? WorkerProcessRunner.RunBufferedAsync;

    internal static IDisposable PushProcessRunner(
        Func<WorkerProcessRunRequest, CancellationToken, Task<WorkerProcessRunResult>> runner)
    {
        var previous = RunnerOverride.Value;
        RunnerOverride.Value = runner;
        return new RestoreAction(() => RunnerOverride.Value = previous);
    }

    internal static string StandardInput(string direction, string decompositionPrompt) =>
        $"Goal: {direction}{Environment.NewLine}" +
        $"Task: {decompositionPrompt}{Environment.NewLine}" +
        $"Task role: {AgentRole.Planner}{Environment.NewLine}" +
        $"Verification plan: Output only a fenced JSON array of nodes with id, objective, and dependsOn fields.{Environment.NewLine}";

    internal static async Task<PlanDecompositionSampleResult> RunAsync(string standardInput, string repositoryRoot,
        ModelFunctionCatalog? catalog,
        Func<WorkerProcessRunRequest, CancellationToken, Task<WorkerProcessRunResult>> runProcessAsync,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var model = ConductorRoundModelResolver.Resolve(catalog, ModelFunctionPurposes.PlanSampler);
            var result = await AuthorBriefDraftRound.DispatchAsync(
                standardInput, repositoryRoot, runProcessAsync, model, cancellationToken);
            return result.ExitCode == 0
                ? new PlanDecompositionSampleResult.Succeeded(result.StandardOutput)
                : new PlanDecompositionSampleResult.Failed(result.ExitCode, Tail(result.StandardError),
                    result.StandardOutput + Environment.NewLine + "--- stderr ---" + Environment.NewLine + result.StandardError);
        }
        catch (Exception ex)
        {
            return new PlanDecompositionSampleResult.Failed(null,
                Tail($"{ex.GetType().Name}: {ex.Message}"), ex.ToString());
        }
    }

    private static string Tail(string text)
    {
        var line = Regex.Replace(text, @"[\r\n]+", " ").Trim();
        return line.Length > 500 ? line[^500..] : line;
    }

    internal static (string? Path, string? Error) SaveRawOutput(
        string directory, DateTimeOffset runStamp, int sampleNumber, string raw)
    {
        try
        {
            Directory.CreateDirectory(directory);
            // Match the Author's byte-tail rule, including UTF-8 output.
            var bytes = Encoding.UTF8.GetBytes(raw);
            var tail = bytes.Length > 65536 ? bytes[^65536..] : bytes;
            var stem = $"{runStamp.UtcDateTime:yyyyMMddTHHmmssZ}-sample-{sampleNumber}";
            for (var collision = 0; ; collision++)
            {
                var suffix = collision == 0 ? "" : $"-r{collision}";
                var path = System.IO.Path.Combine(directory, stem + suffix + ".raw.txt");
                FileStream file;
                try
                {
                    file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                }
                catch (IOException) when (File.Exists(path))
                {
                    // Another run already owns this name. Never overwrite its evidence.
                    continue;
                }
                using (file) file.Write(tail);
                return (path, null);
            }
        }
        catch (Exception ex)
        {
            // Diagnostic I/O must not replace the original sample failure.
            return (null, Tail($"{ex.GetType().Name}: {ex.Message}"));
        }
    }

    private sealed class RestoreAction(Action restore) : IDisposable
    {
        private Action? _restore = restore;
        public void Dispose() => Interlocked.Exchange(ref _restore, null)?.Invoke();
    }
}
