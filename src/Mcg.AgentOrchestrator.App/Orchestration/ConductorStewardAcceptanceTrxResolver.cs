using System.Text.Json;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class ConductorStewardAcceptanceTrxResolver
{
    internal static IReadOnlyList<string> Resolve(string orchestratorDirectory, Goal goal)
    {
        var candidateSha = goal.LatestAcceptanceFailure?.BranchHeadSha;
        if (string.IsNullOrWhiteSpace(candidateSha)) return [];
        var attempts = GoalTerminalReconciliationEvidenceResolver.ResolveForGoal(
            Path.Combine(orchestratorDirectory, "acceptance-gate-attempts"), goal.Id);
        var matches = new List<(DateTimeOffset CompletedAt, IReadOnlyList<string> Paths)>();
        foreach (var attempt in attempts.Where(item => item.State is
                     GoalTerminalReconciliationEvidenceState.Present or
                     GoalTerminalReconciliationEvidenceState.MissingByInProcessProtocol))
        {
            try
            {
                using var metadata = JsonDocument.Parse(File.ReadAllText(attempt.AttemptMetadataPath));
                var root = metadata.RootElement;
                if (!root.TryGetProperty("branchHeadSha", out var sha) || sha.ValueKind != JsonValueKind.String ||
                    !string.Equals(sha.GetString(), candidateSha, StringComparison.OrdinalIgnoreCase) ||
                    !root.TryGetProperty("testResultPaths", out var testPaths) ||
                    testPaths.ValueKind != JsonValueKind.Array)
                    continue;
                var completedAt = root.TryGetProperty("completedAt", out var completed) &&
                                  completed.ValueKind == JsonValueKind.String &&
                                  DateTimeOffset.TryParse(completed.GetString(), out var parsed)
                    ? parsed : DateTimeOffset.MinValue;
                var paths = testPaths.EnumerateArray()
                    .Where(item => item.ValueKind == JsonValueKind.String)
                    .Select(item => item.GetString()!)
                    .Where(item => !string.IsNullOrWhiteSpace(item))
                    .Select(path => Path.IsPathFullyQualified(path)
                        ? path : Path.GetFullPath(path, Path.GetDirectoryName(attempt.AttemptMetadataPath)!))
                    .ToArray();
                matches.Add((completedAt, paths));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
            {
                // Another attempt may still carry the same candidate's complete receipt.
            }
        }
        return matches.OrderByDescending(item => item.CompletedAt).FirstOrDefault().Paths ?? [];
    }
}
