using static Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class CrosswalkJunitParser
{
    internal static async Task<TestResultReadResult> ParseAsync(CrosswalkExecutableIdentity executable,
        string report, string toolName, Func<string[], Task<CommandResult>> runProcess)
    {
        var command = CrosswalkClient.Command(executable.Path, "parse junit", report,
            " --tool '" + toolName.Replace("'", "''") + "'");
        var arguments = new[] { WorkerShell.Executable }.Concat(WorkerShell.BaseArguments()).Append(command).ToArray();
        var process = await runProcess(arguments).ConfigureAwait(false);
        if (process.TimedOut || process.CaptureLimited || process.ExitCode != 0)
            throw new IOException($"JUnit parser failed for '{report}': exit code {process.ExitCode}, timedOut={process.TimedOut}, captureLimited={process.CaptureLimited}. {process.Stderr}");
        var result = new NormalizedTestResultSource().ReadJson(process.StandardOutput ?? "", report);
        if (result.Status != TestResultReadStatus.Readable)
            throw new IOException($"JUnit parser returned invalid results for '{report}': {result.Detail}");
        return result;
    }
}
