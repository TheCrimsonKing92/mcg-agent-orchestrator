using System.Text.Json;
using System.Xml.Linq;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class ProspectiveEvidenceGateResolver
{
    internal static ProspectiveEvidenceGateResolution Resolve(
        AgentOrchestratorKernel kernel,
        Goal goal,
        string orchestratorDirectory,
        string? candidateBranchHeadSha)
    {
        var waits = kernel.GetPendingHumanInput(goal.Id)
            .Where(wait => wait.Kind == HumanWaitKind.ProspectiveAcceptanceEvidence &&
                string.Equals(wait.EvidenceOwner, "acceptance-gate", StringComparison.OrdinalIgnoreCase) &&
                wait.TestNames is { Count: > 0 })
            .ToArray();
        if (waits.Length == 0) return new([], []);

        var attempts = ReadAttempts(orchestratorDirectory, goal.Id, candidateBranchHeadSha);
        var resolved = new List<HumanInputRequestId>();
        var unresolved = new List<ProspectiveEvidenceGateUnresolvedWait>();
        foreach (var wait in waits)
        {
            var names = wait.TestNames!.Distinct(StringComparer.Ordinal).ToArray();
            IReadOnlyList<string> missing = names;
            string? submissionFailure = null;
            foreach (var attempt in attempts)
            {
                var unsatisfied = names.Where(name =>
                {
                    var matches = attempt.Results.Where(result => Matches(name, result.Name)).ToArray();
                    return matches.Length == 0 || matches.Any(result => result.Outcome != "Passed");
                }).ToArray();
                if (attempt == attempts[0]) missing = unsatisfied;
                if (unsatisfied.Length != 0) continue;

                try
                {
                    kernel.SubmitHumanInput(wait.Id,
                        $"Satisfied by gate attempt {attempt.Id}; TRX {string.Join(", ", attempt.Paths)}; {wait.TestNames.Count} named tests passed.",
                        origin: HumanInputAnswerOrigin.Worker, supersedeSiblings: false);
                    resolved.Add(wait.Id);
                }
                catch (Exception exception)
                {
                    // Passing tests do not authorize landing when their answer was not recorded.
                    missing = [];
                    submissionFailure = $"{exception.GetType().Name}: {exception.Message}";
                }
                break;
            }
            if (!resolved.Contains(wait.Id)) unresolved.Add(new(wait.Id, missing, submissionFailure));
        }
        return new(resolved, unresolved);
    }

    private static bool Matches(string selector, string resultName) =>
        string.Equals(selector, resultName, StringComparison.Ordinal) ||
        (resultName.StartsWith(selector, StringComparison.Ordinal) && resultName.Length > selector.Length &&
            resultName[selector.Length] is '(' or '.');

    private static IReadOnlyList<GateAttempt> ReadAttempts(
        string orchestratorDirectory, GoalId goalId, string? candidateSha)
    {
        if (string.IsNullOrWhiteSpace(candidateSha)) return [];
        var attempts = new List<GateAttempt>();
        try
        {
            var directory = Path.Combine(orchestratorDirectory, "acceptance-gate-attempts", goalId.Value);
            if (!Directory.Exists(directory)) return [];
            foreach (var metadataPath in Directory.GetFiles(directory, "*.attempt.json"))
            {
                try
                {
                    using var metadata = JsonDocument.Parse(File.ReadAllText(metadataPath));
                    var root = metadata.RootElement;
                    var id = GetString(root, "attemptId");
                    if (string.IsNullOrWhiteSpace(id) ||
                        !string.Equals(Path.GetFileName(metadataPath), $"{id}.attempt.json", StringComparison.Ordinal) ||
                        !string.Equals(GetString(root, "goalId"), goalId.Value, StringComparison.OrdinalIgnoreCase) ||
                        GetString(root, "kind") != ConductorParallelAcceptanceAttemptCoordinator.GateDispatchKind ||
                        !string.Equals(GetString(root, "branchHeadSha"), candidateSha, StringComparison.OrdinalIgnoreCase) ||
                        !root.TryGetProperty("outcome", out var outcome) || !outcome.TryGetInt32(out var value) ||
                        value != (int)ConductorParallelAcceptanceAttemptOutcome.Passed ||
                        !root.TryGetProperty("testResultPaths", out var testPaths) ||
                        testPaths.ValueKind != JsonValueKind.Array || testPaths.GetArrayLength() == 0)
                        continue;

                    // Follow the steward resolver's metadata-relative path convention. Its
                    // failure-bound selection API cannot select passing explicit candidates.
                    var paths = testPaths.EnumerateArray().Select(path =>
                    {
                        var text = path.GetString();
                        ArgumentException.ThrowIfNullOrWhiteSpace(text);
                        return Path.IsPathFullyQualified(text)
                            ? text : Path.GetFullPath(text, Path.GetDirectoryName(metadataPath)!);
                    }).ToArray();
                    var completedAt = DateTimeOffset.TryParse(GetString(root, "completedAt"), out var completed)
                        ? completed : DateTimeOffset.MinValue;
                    IReadOnlyList<TestResult> results;
                    try
                    {
                        results = paths.SelectMany(ReadResults).ToArray();
                    }
                    catch (Exception)
                    {
                        // Never accept a partial set when any TRX in this attempt is unreadable.
                        results = [];
                    }
                    attempts.Add(new(id, completedAt, paths, results));
                }
                catch (Exception)
                {
                    // Malformed or inaccessible metadata is not passing gate evidence.
                }
            }
        }
        catch (Exception)
        {
            // Missing/inaccessible attempt stores leave the waits pending.
            return [];
        }
        return attempts.OrderByDescending(attempt => attempt.CompletedAt)
            .ThenBy(attempt => attempt.Id, StringComparer.Ordinal).ToArray();
    }

    private static string? GetString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;

    private static IEnumerable<TestResult> ReadResults(string path)
    {
        var document = XDocument.Load(path, LoadOptions.None);
        var definitions = document.Descendants()
            .Where(element => element.Name.LocalName == "UnitTest" && element.Attribute("id") is not null)
            .GroupBy(element => element.Attribute("id")!.Value, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        foreach (var result in document.Descendants().Where(element => element.Name.LocalName == "UnitTestResult"))
        {
            definitions.TryGetValue(result.Attribute("testId")?.Value ?? string.Empty, out var definition);
            var name = AcceptanceTrxTestIdentityResolver.Resolve(result, definition);
            if (name is not null) yield return new(name, result.Attribute("outcome")?.Value);
        }
    }

    private sealed record TestResult(string Name, string? Outcome);
    private sealed record GateAttempt(
        string Id, DateTimeOffset CompletedAt, IReadOnlyList<string> Paths, IReadOnlyList<TestResult> Results);
}

internal sealed record ProspectiveEvidenceGateResolution(
    IReadOnlyList<HumanInputRequestId> ResolvedRequestIds,
    IReadOnlyList<ProspectiveEvidenceGateUnresolvedWait> UnresolvedWaits);

internal sealed record ProspectiveEvidenceGateUnresolvedWait(
    HumanInputRequestId RequestId, IReadOnlyList<string> MissingOrFailedTestNames,
    string? SubmissionFailure = null);
