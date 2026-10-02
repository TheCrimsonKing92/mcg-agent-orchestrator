using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mcg.AgentOrchestrator.Core;

public enum DeveloperCriterionStatus { Proven, NotOwned, Unmet }
public enum DeveloperSelfCheckFieldState { Present, Missing, Malformed }

public sealed record DeveloperCriterionSelfCheck(int CriterionIndex, DeveloperCriterionStatus Status, string Evidence)
{
    public string StatusText => Status switch
    {
        DeveloperCriterionStatus.Proven => "proven",
        DeveloperCriterionStatus.NotOwned => "not-owned",
        DeveloperCriterionStatus.Unmet => "unmet",
        _ => throw new InvalidOperationException("Unknown Developer criterion status.")
    };
}

public sealed record DeveloperCriteriaSelfCheckResult(
    DeveloperSelfCheckFieldState State,
    IReadOnlyList<DeveloperCriterionSelfCheck> Entries,
    string? Reason = null)
{
    public string ToTaskNote(TaskId taskId) =>
        $"DEVELOPER_CRITERIA_SELF_CHECK task={taskId.Value} " +
        $"proven={Entries.Count(e => e.Status == DeveloperCriterionStatus.Proven)} " +
        $"not_owned={Entries.Count(e => e.Status == DeveloperCriterionStatus.NotOwned)} " +
        $"unmet={Entries.Count(e => e.Status == DeveloperCriterionStatus.Unmet)} " +
        $"field={State.ToString().ToLowerInvariant()}" +
        (State == DeveloperSelfCheckFieldState.Malformed ? $" reason={DeveloperCriteriaSelfCheck.OneLine(Reason ?? string.Empty)}" : string.Empty);
}

// Advisory report only: consumers may render this result, never use it to route a round.
public static class DeveloperCriteriaSelfCheck
{
    private sealed record RawEntry(
        [property: JsonPropertyName("criterion_index")] int? CriterionIndex,
        string? Status,
        string? Evidence);

    public static DeveloperCriteriaSelfCheckResult Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return new(DeveloperSelfCheckFieldState.Missing, []);
        if (value.Contains('\r') || value.Contains('\n'))
            return Malformed("JSON must be on one line.");

        try
        {
            var raw = JsonSerializer.Deserialize<List<RawEntry?>>(value,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (raw is null)
                return Malformed("JSON must be an array.");
            var entries = new List<DeveloperCriterionSelfCheck>();
            foreach (var item in raw)
            {
                if (item is null || item.CriterionIndex is not { } index || index < 0)
                    return Malformed("criterion_index must be a non-negative integer.");
                var status = item.Status switch
                {
                    "proven" => DeveloperCriterionStatus.Proven,
                    "not-owned" => DeveloperCriterionStatus.NotOwned,
                    "unmet" => DeveloperCriterionStatus.Unmet,
                    _ => (DeveloperCriterionStatus?)null
                };
                if (status is null)
                    return Malformed("status must be proven, not-owned or unmet.");
                if (string.IsNullOrWhiteSpace(item.Evidence))
                    return Malformed("evidence must be non-empty.");
                entries.Add(new(index, status.Value, item.Evidence));
            }
            return new(DeveloperSelfCheckFieldState.Present, entries);
        }
        catch (JsonException)
        {
            return Malformed("Invalid JSON array or entry shape.");
        }
        catch (NotSupportedException)
        {
            return Malformed("Unsupported JSON entry shape.");
        }
    }

    public static DeveloperCriteriaSelfCheckResult ParseWorkerOutput(string? output)
    {
        List<string>? latestBlock = null;
        var currentBlock = new List<string>();
        var fallback = new List<string>();
        var inBlock = false;
        foreach (var raw in (output ?? string.Empty).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = WorkerResultLineUnwrap.Unwrap(raw.Trim());
            if (line.StartsWith("- ", StringComparison.Ordinal)) line = line[2..].TrimStart();
            var marker = NormalizeMarker(line).TrimEnd(':').Trim();
            if (marker.Equals("WORKER_RESULT", StringComparison.OrdinalIgnoreCase))
            {
                currentBlock.Clear();
                fallback.Clear();
                inBlock = true;
            }
            else if (marker.Equals("END_WORKER_RESULT", StringComparison.OrdinalIgnoreCase))
            {
                if (inBlock) latestBlock = [.. currentBlock];
                inBlock = false;
            }
            else
            {
                fallback.Add(line);
                if (inBlock) currentBlock.Add(line);
            }
        }
        if (inBlock) latestBlock = currentBlock;
        string? value = null;
        foreach (var line in latestBlock ?? fallback)
        {
            var separator = line.IndexOf(':');
            if (separator > 0 && NormalizeMarker(line[..separator]).Equals("criteria_self_check", StringComparison.OrdinalIgnoreCase))
                value = line[(separator + 1)..].Trim();
        }
        return Parse(value);
    }

    public static string OneLine(string value) => value.Replace('\r', ' ').Replace('\n', ' ');

    private static string NormalizeMarker(string value) =>
        value.Replace("#", string.Empty).Replace("*", string.Empty).Replace("`", string.Empty).Trim();

    private static DeveloperCriteriaSelfCheckResult Malformed(string reason) =>
        new(DeveloperSelfCheckFieldState.Malformed, [], reason);
}
