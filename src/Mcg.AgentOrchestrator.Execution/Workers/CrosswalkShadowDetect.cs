using System.Collections.Concurrent;
using System.Text.Json;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal sealed class CrosswalkShadowDetect
{
    internal static CrosswalkShadowDetect Shared { get; } = new(new CrosswalkClient());
    internal const string DisagreementEventKind = "crosswalk-shadow-disagreement";
    internal const string UnavailableEventKind = "crosswalk-shadow-unavailable";

    private readonly CrosswalkClient _client;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly ConcurrentDictionary<string, Lazy<bool>> _roots = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _unavailableStatuses = new(StringComparer.Ordinal);
    private readonly object _appendLock = new();

    internal CrosswalkShadowDetect(CrosswalkClient client, Func<DateTimeOffset>? utcNow = null)
    {
        _client = client;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    internal Toolchain Observe(string workingDirectory, string? projectHomeDirectory, Toolchain orchestratorStack)
    {
        if (string.IsNullOrWhiteSpace(projectHomeDirectory))
            return orchestratorStack;

        try
        {
            var root = Path.GetFullPath(workingDirectory);
            _ = _roots.GetOrAdd(root, _ => new Lazy<bool>(() =>
            {
                // The winning factory owns both detection and the only logging opportunity.
                try { ObserveOnce(root, projectHomeDirectory, orchestratorStack); }
                catch (Exception) { /* Shadow diagnostics must never affect stack resolution. */ }
                return true;
            }, LazyThreadSafetyMode.ExecutionAndPublication)).Value;
        }
        catch (Exception) { /* Includes normalization failures: no usable root means no observation. */ }

        return orchestratorStack;
    }

    private void ObserveOnce(string root, string projectHomeDirectory, Toolchain orchestratorStack)
    {
        var outcome = _client.Detect(root);
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        string eventKind;
        string detail;
        if (outcome.Status == CrosswalkDetectOutcome.Detected && outcome.Result is { } result)
        {
            if (result.Stack == orchestratorStack)
                return;
            eventKind = DisagreementEventKind;
            detail = $"CROSSWALK_SHADOW_DISAGREEMENT root={JsonSerializer.Serialize(root)} " +
                $"orchestrator={orchestratorStack.ToString().ToLowerInvariant()} " +
                $"crosswalk={result.Stack.ToString().ToLowerInvariant()} " +
                $"evidence={JsonSerializer.Serialize(result.EvidenceItems, options)}";
        }
        else if (outcome.Status is CrosswalkDetectOutcome.Missing or CrosswalkDetectOutcome.TimedOut or
            CrosswalkDetectOutcome.StartFailed or CrosswalkDetectOutcome.ExitNonZero or CrosswalkDetectOutcome.InvalidJson)
        {
            if (!_unavailableStatuses.TryAdd(outcome.Status, 0))
                return;
            eventKind = UnavailableEventKind;
            detail = $"CROSSWALK_SHADOW_UNAVAILABLE reason={outcome.Status} " +
                $"root={JsonSerializer.Serialize(root)} detail={JsonSerializer.Serialize(outcome.Detail)}";
        }
        else
            return;

        lock (_appendLock)
        {
            try
            {
                var record = new ConductEventMirrorRecord(_utcNow(), eventKind, null, detail);
                var directory = Path.Combine(projectHomeDirectory, "logs");
                Directory.CreateDirectory(directory);
                File.AppendAllText(Path.Combine(directory, "conduct-events.log"),
                    JsonSerializer.Serialize(record, options) + Environment.NewLine);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
