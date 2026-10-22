using System.Globalization;
using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class CliExecutorScreenCommand
{
    internal static bool IsCommand(IReadOnlyList<string> args) => args.Count > 0 &&
        args[0].Equals("executor-screen", StringComparison.OrdinalIgnoreCase);

    internal static Task<int> RunAsync(IReadOnlyList<string> args, OrchestratorWorkspace workspace)
    {
        var root = AcceptancePartitionVerdictCache.ResolveHostStateRoot(workspace.ExecutionDirectory);
        return RunAsync(args, RemoteLaneExecutorConfiguration.ResolveStorePath(root),
            () => ReadCandidates(workspace), ReadLanes,
            (configuration, worktree, prefix) => new SshRemoteLaneExecutor(configuration, worktree, prefix,
                TimeProvider.System, GoalAcceptanceVerifier.RunRemoteLaneTransportAsync, GitCli.Run),
            TimeProvider.System, TimeSpan.FromSeconds(1), root,
            ExecutorScreeningReceiptLedger.ResolveStorePath(root), Console.Out, Console.Error);
    }

    internal static async Task<int> RunAsync(IReadOnlyList<string> args, string configurationPath,
        Func<IReadOnlyList<ExecutorScreeningCandidate>> candidateSource,
        Func<string, (IReadOnlyList<AcceptanceTestLane> Lanes, TimeSpan LaneTimeout)> laneSource,
        Func<RemoteLaneExecutorConfiguration, string, string, IRemoteLaneExecutor> executorFactory,
        TimeProvider clock, TimeSpan pollInterval, string occupancyRoot, string receiptPath,
        TextWriter output, TextWriter error)
    {
        var maximum = 1;
        string? executorId = null;
        for (var i = 1; i < args.Count; i++)
        {
            if (args[i] is "--help" or "-h") { output.WriteLine(CliCommandHelp.ExecutorScreenUsage); return 0; }
            if (args[i] is not ("--max-candidates" or "--executor") || i + 1 == args.Count) return Usage();
            var flag = args[i++];
            if (flag == "--executor") executorId = args[i];
            else if (!int.TryParse(args[i], NumberStyles.None, CultureInfo.InvariantCulture, out maximum) || maximum is < 1 or > 50)
                return Usage();
        }
        var configuration = RemoteLaneExecutorConfiguration.Load(configurationPath);
        var refusal = !configuration.Enabled ? "remote-lanes-disabled" : configuration.Screening.FaultReason == "invalid"
            ? "screening-invalid" : configuration.Screening.Mode != "shadow" ? "screening-off" : null;
        if (refusal is not null) { output.WriteLine("SCREEN refused reason=" + refusal); return 2; }
        if (executorId is not null && !configuration.Executors.Any(entry => entry.Id == executorId && entry.Transport == "ssh"))
            return Usage();
        for (var iteration = 0; iteration < maximum; iteration++)
        {
            var entry = ExecutorScreeningPlanner.PickIdleExecutor(configuration.Executors,
                id => RemoteExecutorOccupancy.IsOccupied(occupancyRoot, id), executorId);
            if (entry is null) return Idle(configuration.Executors.Any(executor => executor.AdmissionEnabled &&
                executor.Transport == "ssh" && (executorId is null || executor.Id == executorId))
                ? "no-idle-executor" : "no-enabled-executor");
            var candidate = ExecutorScreeningPlanner.PickCandidate(candidateSource(), ExecutorScreeningReceiptLedger.ReadAll(receiptPath));
            if (candidate is null) return Idle("no-candidate");
            IReadOnlyList<AcceptanceTestLane> lanes = [];
            var timeout = new AcceptanceGateEngineSettings().ResolveCheckTimeout(null);
            try { (lanes, timeout) = laneSource(candidate.WorktreePath); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or ArgumentException)
            { /* An unreadable manifest retires this candidate with an incomplete receipt. */ }
            var selection = ExecutorScreeningPlanner.SelectLanes(lanes, configuration);
            var id = Guid.NewGuid().ToString("N");
            var prefix = Path.Combine(occupancyRoot, ".orchestrator", "executor-screening", id, "result");
            try
            {
                var receipt = await ExecutorScreeningRunner.RunAsync(candidate, entry, selection, configuration,
                    executorFactory(configuration, candidate.WorktreePath, prefix), occupancyRoot, receiptPath,
                    id, timeout, clock, pollInterval).ConfigureAwait(false);
                if (receipt is null) return Idle("no-idle-executor");
                output.WriteLine($"SCREEN goal={candidate.GoalId[..Math.Min(8, candidate.GoalId.Length)]} " +
                    $"commit={candidate.Commit[..Math.Min(9, candidate.Commit.Length)]} executor={entry.Id} " +
                    $"verdict={receipt.Verdict.ToString().ToLowerInvariant()} lanes={receipt.LaneOutcomes.Count} " +
                    $"failing={receipt.LaneOutcomes.Count(lane => lane.FailingTests.Count > 0)} " +
                    $"excluded={receipt.ExcludedLanes.Count} receipt={receipt.ReceiptId}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { error.WriteLine($"SCREEN error={ex.GetType().Name}: {ex.Message}"); return 1; }
        }
        return 0;
        int Usage() { error.WriteLine(CliCommandHelp.ExecutorScreenUsage); return 2; }
        int Idle(string reason) { output.WriteLine("SCREEN idle reason=" + reason); return 0; }
    }

    private static (IReadOnlyList<AcceptanceTestLane>, TimeSpan) ReadLanes(string worktree)
    {
        var settings = AcceptanceGateEngineSettings.Load(worktree);
        var check = settings.DeclaredDotnetTestChecks.FirstOrDefault(check => check.Project is { } project &&
            Path.GetFileName(project.Replace('\\', '/')).Equals("Mcg.AgentOrchestrator.Infrastructure.Tests.csproj", StringComparison.OrdinalIgnoreCase));
        return (AcceptanceLaneMembership.ResolveOwnedCollections(settings.InfrastructureTestLanes, worktree),
            settings.ResolveCheckTimeout(check?.TimeoutMinutes));
    }

    private static IReadOnlyList<ExecutorScreeningCandidate> ReadCandidates(OrchestratorWorkspace workspace)
    {
        var main = Scalar(workspace.ExecutionDirectory, "rev-parse", "refs/heads/" + workspace.IntegrationBranch);
        if (main is null) return [];
        var candidates = new List<ExecutorScreeningCandidate>();
        foreach (var goal in ExperimentGoals.Read(workspace.SqliteStatePath))
        {
            if (goal.Status is not (GoalStatus.Active or GoalStatus.Verified)) continue;
            var developers = goal.Tasks.Where(task => task.RequiredRole == AgentRole.Developer).ToArray();
            if (developers.Length == 0 || developers.Any(task => task.Status != WorkTaskStatus.Completed)) continue;
            var worktree = GoalWorktrees.TryResolve(workspace.ExecutionDirectory, goal.Id);
            if (worktree is null) continue;
            var head = Scalar(worktree, "rev-parse", "HEAD");
            var tree = Scalar(worktree, "rev-parse", "HEAD^{tree}");
            var committed = Scalar(worktree, "log", "-1", "--format=%cI", "HEAD");
            var status = GitCli.Run(worktree, GitCli.DefaultTimeoutMilliseconds, "status", "--porcelain", "--untracked-files=all");
            if (head is null || tree is null || !DateTimeOffset.TryParse(committed, CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out var at) || !status.ProcessStarted || !status.Succeeded || status.DrainTimedOut) continue;
            candidates.Add(new(goal.Id.Value, goal.Status, head, tree, main, at, string.IsNullOrWhiteSpace(status.Output), true, worktree));
        }
        return candidates;
    }

    private static string? Scalar(string directory, params string[] args)
    {
        var result = GitCli.Run(directory, GitCli.DefaultTimeoutMilliseconds, args);
        return result.ProcessStarted && result.Succeeded && !result.DrainTimedOut && !string.IsNullOrWhiteSpace(result.Output)
            ? result.Output.Trim() : null;
    }
}
