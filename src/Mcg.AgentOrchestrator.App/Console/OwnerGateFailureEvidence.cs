using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal sealed class OwnerGateFailureEvidence(string orchestratorDirectory)
{
    internal OwnerActivityTestEvidence? Read(OwnerConductEvent item)
    {
        var attempt = OwnerActivityNarrator.Field(item, "attempt");
        var prefix = item.GoalId;
        var root = Path.Combine(orchestratorDirectory, "acceptance-gate-attempts");
        if (attempt is null || prefix is null || !Directory.Exists(root) ||
            attempt.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return null;
        try
        {
            var matches = Directory.EnumerateDirectories(root).Where(path =>
                Path.GetFileName(path).StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).Take(2).ToArray();
            if (matches.Length != 1) return null;
            var metadata = Path.Combine(matches[0], attempt + ".attempt.json");
            if (!File.Exists(metadata)) return null;
            var receipt = GoalTerminalReconciliationEvidenceResolver.Resolve(metadata);
            if (receipt.State is not (GoalTerminalReconciliationEvidenceState.Present or
                GoalTerminalReconciliationEvidenceState.MissingByInProcessProtocol)) return null;
            using var document = JsonDocument.Parse(File.ReadAllText(metadata));
            if (!document.RootElement.TryGetProperty("testResultPaths", out var paths) || paths.ValueKind != JsonValueKind.Array)
                return null;
            var failures = paths.EnumerateArray().Where(path => path.ValueKind == JsonValueKind.String)
                .Select(path => path.GetString()).OfType<string>().Where(path => !string.IsNullOrWhiteSpace(path))
                .SelectMany(path => AcceptanceTrxFailureReader.Read(Path.GetFullPath(path, matches[0])).Failures).ToArray();
            return failures.Length == 0 ? null : new(failures.Select(failure => failure.TestName).OfType<string>().ToArray(),
                [], FirstFailure: Describe(failures[0]));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        { return null; }
    }

    internal static string Describe(AcceptanceTrxFailure failure)
    {
        var name = failure.TestName?.Split('(', 2)[0];
        name = name is null ? "" : string.Join(".", name.Split('.').TakeLast(2));
        var message = OwnerHoldReason.FirstLine(failure.Message);
        return string.Join(": ", new[] { name, message }.Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => string.Join(" ", value!.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))));
    }
}
