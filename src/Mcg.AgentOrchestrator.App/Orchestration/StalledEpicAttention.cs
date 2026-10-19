using System.Security.Cryptography;
using System.Text;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record StalledEpicCandidate(string EpicId, string Title, EpicPlanView View);

internal sealed class StalledEpicAttention
{
    private const string Prefix = "stalled-epic:";
    private readonly Func<IReadOnlyList<StalledEpicCandidate>> _candidates;
    private readonly ICollaborationItemStore _store;
    private readonly ConductEventLogWriter _events;
    private readonly TimeSpan _interval;
    private DateTimeOffset? _lastRun;
    private string? _lastReadFailure;

    internal StalledEpicAttention(Func<IReadOnlyList<StalledEpicCandidate>> candidates,
        ICollaborationItemStore store, ConductEventLogWriter events, TimeSpan? interval = null)
    {
        _candidates = candidates;
        _store = store;
        _events = events;
        _interval = interval ?? TimeSpan.FromMinutes(15);
    }

    internal int Service(DateTimeOffset now)
    {
        if (_lastRun is { } last && now - last < _interval) return 0;
        _lastRun = now;
        IReadOnlyList<StalledEpicCandidate> candidates;
        try { candidates = _candidates(); }
        catch (Exception exception)
        {
            var detail = $"EPIC_STALLED_READ_FAILED reason={exception.GetType().Name}: {exception.Message}"
                .Replace('\r', ' ').Replace('\n', ' ');
            if (detail != _lastReadFailure && _events.AppendRequired("epic-stalled", null, detail, now))
                _lastReadFailure = detail;
            return 0;
        }
        _lastReadFailure = null;
        var items = _store.ListAsync().GetAwaiter().GetResult();
        var knownKeys = items.Select(item => item.CorrelationKey).ToHashSet(StringComparer.Ordinal);
        var open = items.Where(item => !CollaborationItemLifecycle.IsTerminal(item.Status) &&
            item.CorrelationKey?.StartsWith(Prefix, StringComparison.Ordinal) == true).ToArray();
        var stalledPrefixes = new HashSet<string>(StringComparer.Ordinal);
        var changes = 0;
        foreach (var candidate in candidates)
        {
            if (!EpicPlanNextStep.Compute(candidate.View.Items).IsStalled) continue;
            var incomplete = candidate.View.Items.Where(item => item.Item.Kind == EpicPlanItemKind.Step
                ? !item.Item.Done : !item.ScopeComplete && item.Status != EpicPlanSliceStatus.Superseded)
                .OrderBy(item => item.Item.Position).ToArray();
            var signatureText = string.Join(";", incomplete.Select(item =>
                $"{item.Item.Position}:{item.Status?.ToString() ?? (item.Item.Done ? "done" : "open")}"));
            var signature = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(signatureText)))[..12];
            var epicPrefix = $"{Prefix}{candidate.EpicId}:";
            stalledPrefixes.Add(epicPrefix);
            var key = epicPrefix + signature;
            if (knownKeys.Add(key))
            {
                var label = $"{candidate.EpicId[..Math.Min(8, candidate.EpicId.Length)]} {candidate.Title}";
                var lines = new List<string> { $"Epic: {label}", $"State: {EpicPlanNextStep.StalledText}",
                    $"Open items: {incomplete.Length}" };
                lines.AddRange(incomplete.Select(item => $"{item.Item.Position}. {item.Subject} — {item.Render()}"));
                lines.Add($"Next: file the next slice or record a plan decision; read epic-plan {candidate.EpicId[..Math.Min(8, candidate.EpicId.Length)]}");
                _store.RaiseAsync(CollaborationItemType.Decision, goalId: null, $"Epic stalled: {label}",
                    string.Join("\n", lines), key).GetAwaiter().GetResult();
                changes++;
            }
            foreach (var item in open.Where(item => item.CorrelationKey!.StartsWith(epicPrefix, StringComparison.Ordinal) &&
                item.CorrelationKey != key))
                if (_store.TryResolveAsync(item.CorrelationKey!, "stalled set changed").GetAwaiter().GetResult()) changes++;
        }
        foreach (var item in open.Where(item => !stalledPrefixes.Any(prefix =>
            item.CorrelationKey!.StartsWith(prefix, StringComparison.Ordinal))))
            if (_store.TryResolveAsync(item.CorrelationKey!, "epic no longer stalled").GetAwaiter().GetResult()) changes++;
        return changes;
    }

    internal static IReadOnlyList<StalledEpicCandidate> ReadCandidates(OrchestratorWorkspace workspace)
    {
        if (!File.Exists(workspace.PortfolioStorePath)) return [];
        var epics = PortfolioStore.OpenReadOnly(workspace.PortfolioStorePath).ListEpicsAsync().GetAwaiter().GetResult();
        var views = EpicPlanStatusReader.Load(workspace, epics);
        return epics.Select(epic => new StalledEpicCandidate(epic.Id, epic.Title, views[epic.Id])).ToArray();
    }
}
