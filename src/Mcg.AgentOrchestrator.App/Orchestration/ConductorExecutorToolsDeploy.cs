using System.Globalization;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// One background reconciler owns deployment state for the lifetime of a conductor.
internal sealed class ConductorExecutorToolsDeploy(
    string hostStateRoot, string configurationPath, TimeProvider clock,
    Func<string[], string, TimeSpan, CancellationToken, Task<GoalAcceptanceVerifier.CommandResult>> transport,
    Func<int, DateTimeOffset?> startTimeOf, ConductEventLogWriter writer, Func<ExecutorToolsSource?> readMainTools)
{
    private sealed record ManifestRead(DateTimeOffset At, string MainSha, string? Digest);
    private readonly Dictionary<string, ManifestRead> _reads = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Executor, string Digest), DateTimeOffset> _failures = [];
    private DateTimeOffset? _lastStart;
    private bool _reportedUnavailable;
    internal Task? CurrentRun { get; private set; }

    internal static ConductorExecutorToolsDeploy ForWorkspace(OrchestratorWorkspace workspace, ConductEventLogWriter writer)
    {
        var root = AcceptancePartitionVerdictCache.ResolveHostStateRoot(workspace.ExecutionDirectory);
        return new(root, RemoteLaneExecutorConfiguration.ResolveStorePath(workspace.ExecutionDirectory), TimeProvider.System,
            GoalAcceptanceVerifier.RunRemoteLaneTransportAsync, RemoteExecutorOccupancy.DefaultStartTime, writer,
            () => ExecutorToolsMainSource.Materialize(workspace.ExecutionDirectory, workspace.IntegrationBranch, root, GitCli.Run));
    }

    internal void Evaluate()
    {
        if (CurrentRun is { IsCompleted: false }) return;
        var now = clock.GetUtcNow();
        if (_lastStart is { } last && now - last < TimeSpan.FromMinutes(2)) return;
        _lastStart = now;
        CurrentRun = Task.Run(Run);
    }

    private void Run()
    {
        try
        {
            var entries = RemoteLaneExecutorConfiguration.LoadExecutors(configurationPath).Where(entry =>
                entry.Transport == "ssh" && entry.AdminAlias is not null && entry.RunRoot is not null && entry.ToolsDigestCheck).ToArray();
            if (entries.Length == 0) return;
            var source = readMainTools();
            if (source is null)
            {
                if (!_reportedUnavailable)
                {
                    Append("EXECUTOR_TOOLS_DEPLOY outcome=failed reason=main-tools-unavailable main=none");
                    _reportedUnavailable = true;
                }
                return;
            }
            _reportedUnavailable = false;
            var deploy = new RemoteExecutorToolsDeploy(transport, TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2));
            foreach (var entry in entries)
            {
                if (RemoteExecutorOccupancy.IsOccupied(hostStateRoot, entry.Id, startTimeOf)) continue;
                var now = clock.GetUtcNow();
                if (!_reads.TryGetValue(entry.Id, out var read) || read.MainSha != source.MainSha || now - read.At >= TimeSpan.FromMinutes(10))
                {
                    var folder = Path.Combine(hostStateRoot, ".orchestrator", "executor-tools-deploy", "manifest-" + entry.Id);
                    read = new(now, source.MainSha, deploy.ReadDeployedDigest(entry, folder));
                    _reads[entry.Id] = read;
                }
                if (string.Equals(read.Digest, source.Digest, StringComparison.OrdinalIgnoreCase) ||
                    (_failures.TryGetValue((entry.Id, source.Digest), out var failedAt) && now - failedAt < TimeSpan.FromMinutes(10))) continue;
                var attempt = "tools-deploy-" + now.ToString("yyyyMMddTHHmmssfffZ", CultureInfo.InvariantCulture);
                var claims = new List<IDisposable>();
                try
                {
                    for (var slot = 0; slot < entry.Slots; slot++)
                    {
                        var claim = RemoteExecutorOccupancy.TryClaimExclusive(hostStateRoot, entry.Id, slot);
                        if (claim is null) break;
                        claims.Add(claim);
                        RemoteExecutorOccupancy.Claim(hostStateRoot, entry.Id, attempt, slot);
                    }
                    if (claims.Count != entry.Slots) continue;
                    var backup = "tools.bak-" + now.ToString("yyyyMMddTHHmmssZ", CultureInfo.InvariantCulture);
                    var result = deploy.Deploy(entry, source, backup);
                    var prefix = $"EXECUTOR_TOOLS_DEPLOY executor={entry.Id} outcome={result.Outcome}";
                    var main = source.MainSha[..Math.Min(12, source.MainSha.Length)];
                    if (result.Outcome == "deployed")
                    {
                        _reads[entry.Id] = read with { Digest = result.Readback };
                        Append($"{prefix} old={result.Previous ?? "none"} new={result.Readback} main={main} backup={result.Backup ?? backup}");
                    }
                    else
                    {
                        _failures[(entry.Id, source.Digest)] = clock.GetUtcNow();
                        Append($"{prefix} reason={result.Reason} old={result.Previous ?? read.Digest ?? "none"} new={source.Digest} main={main}");
                    }
                }
                finally
                {
                    for (var slot = 0; slot < claims.Count; slot++)
                    {
                        RemoteExecutorOccupancy.Release(hostStateRoot, entry.Id, attempt, slot);
                        claims[slot].Dispose();
                    }
                }
            }
        }
        catch (Exception) { /* Background deployment must not fail the conductor tick. */ }
    }

    private void Append(string detail) => writer.AppendRequired("executor-tools-deploy", null, detail,
        clock.GetUtcNow(), $"executor-tools-deploy:{Guid.NewGuid():N}");
}
