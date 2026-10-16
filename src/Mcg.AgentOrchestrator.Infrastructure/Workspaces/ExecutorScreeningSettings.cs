using System.Text.Json;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal sealed record ExecutorScreeningSettings
{
    public string Mode { get; init; } = "off";
    public IReadOnlyList<string> KnownDivergentTests { get; init; } = [];
    public string? FaultReason { get; init; }

    internal static ExecutorScreeningSettings Parse(JsonElement root)
    {
        if (!root.TryGetProperty("screening", out var block)) return new();
        var invalid = new ExecutorScreeningSettings { FaultReason = "invalid" };
        if (block.ValueKind != JsonValueKind.Object) return invalid;
        var mode = "off";
        if (block.TryGetProperty("mode", out var value))
        {
            if (value.ValueKind != JsonValueKind.String || value.GetString() is not ("off" or "shadow"))
                return invalid;
            mode = value.GetString()!;
        }
        var tests = new List<string>();
        if (block.TryGetProperty("knownDivergentTests", out var array))
        {
            if (array.ValueKind != JsonValueKind.Array) return invalid;
            foreach (var test in array.EnumerateArray())
            {
                if (test.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(test.GetString()))
                    return invalid;
                tests.Add(test.GetString()!);
            }
        }
        return new() { Mode = mode, KnownDivergentTests = tests.ToArray() };
    }

    internal bool IsKnownDivergent(string identity) => KnownDivergentTests.Any(entry =>
        identity.Equals(entry, StringComparison.Ordinal) || identity.EndsWith("." + entry, StringComparison.Ordinal));
}
