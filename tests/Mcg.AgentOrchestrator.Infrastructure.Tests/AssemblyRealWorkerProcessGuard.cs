using System.Diagnostics;
using System.Runtime.CompilerServices;
using Mcg.AgentOrchestrator.Infrastructure;

internal static class AssemblyRealWorkerProcessGuard
{
    private static RealWorkerProcessSnapshot _assemblyBaseline = RealWorkerProcessGuard.CaptureSnapshot();

    [ModuleInitializer]
    internal static void Install()
    {
        Environment.SetEnvironmentVariable(
            BackgroundDispatchRunner.TestRewriteRealWorkerCommandsVariable,
            "1",
            EnvironmentVariableTarget.Process);

        _assemblyBaseline = RealWorkerProcessGuard.CaptureSnapshot();
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            if (!_assemblyBaseline.CommandLineEnumerationAvailable)
            {
                Console.Error.WriteLine(RealWorkerProcessGuard.CommandLineEnumerationUnavailableMessage);
                return;
            }

            var matches = RealWorkerProcessGuard.FindNewMatches(_assemblyBaseline);
            if (matches.Count == 0)
            {
                return;
            }

            Console.Error.WriteLine(RealWorkerProcessGuard.FormatFailure(matches));
            Environment.ExitCode = 1;
        };
    }
}

internal sealed record RealWorkerProcessSnapshot(
    bool CommandLineEnumerationAvailable,
    IReadOnlySet<int> MatchingProcessIds);

internal sealed record RealWorkerProcessMatch(int ProcessId, string CommandLine);

internal static class RealWorkerProcessGuard
{
    public const string CommandLineEnumerationUnavailableMessage =
        "Skipping real-worker process guard: process command-line enumeration is unavailable on this platform.";

    private static readonly string[] ScopedTempRootMarkers =
    [
        "mcg-orchestrator-tests",
        "mcg-conductor-driver",
        ".orchestrator-worktrees",
        DotnetBuildEnvironmentManager.RootDirectoryName,
        "mcg-chaos-tests"
    ];

    private static readonly string[] RealWorkerCommandSignatures =
    [
        "codex exec",
        "@openai/codex",
        "gpt-5.3-codex-spark",
        "gpt-5-codex",
        "gpt-5.5-codex",
        "claude -p",
        "claude --print",
        "claude --model"
    ];

    public static RealWorkerProcessSnapshot CaptureSnapshot()
    {
        var matches = FindCurrentMatches(out var available);
        return new RealWorkerProcessSnapshot(
            available,
            matches.Select(match => match.ProcessId).ToHashSet());
    }

    public static IReadOnlyList<RealWorkerProcessMatch> FindNewMatches(RealWorkerProcessSnapshot baseline)
    {
        if (!baseline.CommandLineEnumerationAvailable)
        {
            return [];
        }

        return FindCurrentMatches(out _)
            .Where(match => !baseline.MatchingProcessIds.Contains(match.ProcessId))
            .ToArray();
    }

    public static void AssertNoNewMatches(RealWorkerProcessSnapshot baseline)
    {
        if (!baseline.CommandLineEnumerationAvailable)
        {
            Console.Error.WriteLine(CommandLineEnumerationUnavailableMessage);
            return;
        }

        var matches = FindNewMatches(baseline);
        Assert.True(matches.Count == 0, FormatFailure(matches));
    }

    public static string FormatFailure(IReadOnlyList<RealWorkerProcessMatch> matches)
    {
        var lines = new List<string>
        {
            "Infrastructure tests spawned scoped real worker process command lines:"
        };

        lines.AddRange(matches.Select(match => $"pid={match.ProcessId} commandLine={match.CommandLine}"));
        return string.Join(Environment.NewLine, lines);
    }

    private static IReadOnlyList<RealWorkerProcessMatch> FindCurrentMatches(out bool commandLineEnumerationAvailable)
    {
        commandLineEnumerationAvailable = ProcessCommandLines.Read([Environment.ProcessId]).ContainsKey(Environment.ProcessId);
        if (!commandLineEnumerationAvailable)
        {
            return [];
        }

        int[] pids;
        try
        {
            pids = Process.GetProcesses().Select(process => process.Id).ToArray();
        }
        catch
        {
            return [];
        }

        var commandLines = ProcessCommandLines.Snapshot().Read(pids);
        return commandLines
            .Where(pair => IsScopedToTestTempRoot(pair.Value) && LooksLikeRealWorkerCommandLine(pair.Value))
            .Select(pair => new RealWorkerProcessMatch(pair.Key, pair.Value))
            .ToArray();
    }

    private static bool IsScopedToTestTempRoot(string commandLine) =>
        ScopedTempRootMarkers.Any(marker => commandLine.Contains(marker, StringComparison.OrdinalIgnoreCase));

    private static bool LooksLikeRealWorkerCommandLine(string commandLine) =>
        RealWorkerCommandSignatures.Any(signature => commandLine.Contains(signature, StringComparison.OrdinalIgnoreCase));
}
