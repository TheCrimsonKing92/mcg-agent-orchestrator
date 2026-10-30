using System.Text.Json;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// Reads the paired attempt receipts directly; never opens a writable state store.
internal static class ImpactReplayAttemptSource
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    internal static IReadOnlyList<ImpactReplayStoredAttempt> Read(string orchestratorDirectory,
        DateTimeOffset? since, List<ImpactReplayDiagnostic> skipped)
    {
        var directory = Path.Combine(orchestratorDirectory, "acceptance-gate-attempts");
        if (!Directory.Exists(directory)) return [];
        var attempts = new List<ImpactReplayStoredAttempt>();
        foreach (var path in Directory.EnumerateFiles(directory, "*.attempt.json", SearchOption.AllDirectories)
                     .Order(StringComparer.Ordinal))
        {
            try
            {
                var attempt = JsonSerializer.Deserialize<ConductorParallelAcceptanceAttempt>(File.ReadAllText(path), Json)
                    ?? throw new InvalidDataException("Null attempt receipt.");
                if (since is { } start && attempt.StartedAt < start) continue;
                if (attempt.Kind != "gate") { Skip("Not a solo gate attempt."); continue; }
                var resultPath = path[..^".attempt.json".Length] + ".result.json";
                var result = JsonSerializer.Deserialize<ConductorParallelAcceptanceRunArtifact>(File.ReadAllText(resultPath), Json)
                    ?? throw new InvalidDataException("Null result receipt.");
                var branch = result.BranchHeadSha ?? result.Acceptance?.BranchHeadSha ?? attempt.BranchHeadSha;
                var main = result.MainHeadSha ?? result.Acceptance?.MainHeadSha ?? attempt.MainHeadSha;
                if (!IsCommit(branch) || !IsCommit(main)) { Skip("Missing or invalid commit SHA."); continue; }
                if (string.IsNullOrWhiteSpace(attempt.AttemptId) || string.IsNullOrWhiteSpace(attempt.GoalId) ||
                    attempt.StartedAt == default) { Skip("Missing attempt identity or start time."); continue; }
                var paths = (attempt.TestResultPaths ?? []).Concat(result.Acceptance?.TestResultPaths ?? [])
                    .Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.Ordinal).ToArray();
                attempts.Add(new(attempt.AttemptId, attempt.GoalId, attempt.StartedAt, main!, branch!,
                    Path.GetDirectoryName(path)!, paths, result.Acceptance?.FailedChecks ?? []));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException or InvalidDataException)
            { Skip($"Unreadable attempt/result: {ex.Message}"); }
            void Skip(string reason) => skipped.Add(new(path, reason));
        }
        return attempts.OrderByDescending(attempt => attempt.StartedAt)
            .ThenBy(attempt => attempt.AttemptId, StringComparer.Ordinal).ToArray();
    }

    private static bool IsCommit(string? value) => value is { Length: 40 or 64 } && value.All(Uri.IsHexDigit);
}
