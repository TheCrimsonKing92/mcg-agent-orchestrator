using System.Globalization;
using System.Text.Json;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal enum GoalTerminalReconciliationEvidenceState
{
    Present,
    MissingByInProcessProtocol,
    Unreadable,
    Invalid,
    Contradictory
}

internal sealed record GoalTerminalReconciliationEvidence(
    string AttemptId,
    GoalTerminalReconciliationEvidenceState State,
    string AttemptMetadataPath,
    string TypedResultPath,
    string RawExitPath,
    string? TypedKind,
    bool? AcceptancePassed,
    int? RawExitCode,
    string Detail);

internal static class GoalTerminalReconciliationEvidenceResolver
{
    public static IReadOnlyList<GoalTerminalReconciliationEvidence> ResolveForGoal(
        string acceptanceAttemptsDirectory,
        GoalId goalId)
    {
        var directory = Path.Combine(
            Path.GetFullPath(acceptanceAttemptsDirectory),
            goalId.Value);
        if (!Directory.Exists(directory))
        {
            return [];
        }

        return Directory.EnumerateFiles(directory, "*.attempt.json")
            .OrderBy(path => Path.GetFileName(path), StringComparer.Ordinal)
            .Select(Resolve)
            .ToArray();
    }

    public static GoalTerminalReconciliationEvidence Resolve(string attemptMetadataPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(attemptMetadataPath);
        var metadataPath = Path.GetFullPath(attemptMetadataPath);
        var fileName = Path.GetFileName(metadataPath);
        if (!fileName.EndsWith(".attempt.json", StringComparison.OrdinalIgnoreCase))
        {
            return Invalid(metadataPath, "Attempt metadata path must end with .attempt.json.");
        }

        var attemptId = fileName[..^".attempt.json".Length];
        var prefix = Path.Combine(Path.GetDirectoryName(metadataPath)!, attemptId);
        var resultPath = prefix + ".result.json";
        var exitPath = prefix + ".exit.txt";

        try
        {
            using var metadata = JsonDocument.Parse(File.ReadAllText(metadataPath));
            if (!TryGetString(metadata.RootElement, "attemptId", out var recordedAttemptId) ||
                !string.Equals(recordedAttemptId, attemptId, StringComparison.Ordinal))
            {
                return Invalid(metadataPath, "Attempt metadata identity does not match its directory-scoped file stem.");
            }

            if (!File.Exists(resultPath))
            {
                return Evidence(
                    attemptId,
                    GoalTerminalReconciliationEvidenceState.Invalid,
                    metadataPath,
                    resultPath,
                    exitPath,
                    null,
                    null,
                    null,
                    "The attempt-scoped typed result is missing.");
            }

            using var typed = JsonDocument.Parse(File.ReadAllText(resultPath));
            if (!TryGetString(typed.RootElement, "kind", out var kind))
            {
                return Evidence(
                    attemptId,
                    GoalTerminalReconciliationEvidenceState.Invalid,
                    metadataPath,
                    resultPath,
                    exitPath,
                    null,
                    null,
                    null,
                    "The attempt-scoped typed result has no kind.");
            }

            var acceptancePassed = TryGetNestedBoolean(typed.RootElement, "acceptance", "passed");
            if (!File.Exists(exitPath))
            {
                var inProcessProtocol = TryGetString(metadata.RootElement, "executionProtocol", out var executionProtocol) &&
                    executionProtocol.Equals("in-process", StringComparison.OrdinalIgnoreCase);
                return Evidence(
                    attemptId,
                    inProcessProtocol
                        ? GoalTerminalReconciliationEvidenceState.MissingByInProcessProtocol
                        : GoalTerminalReconciliationEvidenceState.Invalid,
                    metadataPath,
                    resultPath,
                    exitPath,
                    kind,
                    acceptancePassed,
                    null,
                    inProcessProtocol
                        ? "The in-process attempt protocol did not produce a raw exit sidecar."
                        : "The out-of-process attempt is missing its raw exit sidecar.");
            }

            var rawExitText = File.ReadAllText(exitPath).Trim();
            if (!int.TryParse(rawExitText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var rawExitCode))
            {
                return Evidence(
                    attemptId,
                    GoalTerminalReconciliationEvidenceState.Invalid,
                    metadataPath,
                    resultPath,
                    exitPath,
                    kind,
                    acceptancePassed,
                    null,
                    "The attempt-scoped raw exit sidecar is not an integer.");
            }

            var acceptedKind = kind.Equals("accepted", StringComparison.OrdinalIgnoreCase);
            if (acceptedKind && acceptancePassed is null)
            {
                return Evidence(
                    attemptId,
                    GoalTerminalReconciliationEvidenceState.Invalid,
                    metadataPath,
                    resultPath,
                    exitPath,
                    kind,
                    acceptancePassed,
                    rawExitCode,
                    "The accepted typed result has no acceptance.passed verdict.");
            }

            var contradictory =
                (!acceptedKind && acceptancePassed == true) ||
                (acceptedKind && acceptancePassed == true && rawExitCode != 0) ||
                ((acceptancePassed == false || !acceptedKind) && rawExitCode == 0);
            return Evidence(
                attemptId,
                contradictory
                    ? GoalTerminalReconciliationEvidenceState.Contradictory
                    : GoalTerminalReconciliationEvidenceState.Present,
                metadataPath,
                resultPath,
                exitPath,
                kind,
                acceptancePassed,
                rawExitCode,
                contradictory
                    ? "The typed result and raw exit sidecar disagree within the same attempt identity."
                    : "The attempt-scoped typed result and raw exit sidecar agree.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Evidence(
                attemptId,
                GoalTerminalReconciliationEvidenceState.Unreadable,
                metadataPath,
                resultPath,
                exitPath,
                null,
                null,
                null,
                $"Attempt-scoped evidence could not be read: {ex.GetType().Name}: {ex.Message}");
        }
        catch (JsonException ex)
        {
            return Evidence(
                attemptId,
                GoalTerminalReconciliationEvidenceState.Invalid,
                metadataPath,
                resultPath,
                exitPath,
                null,
                null,
                null,
                $"Attempt-scoped JSON is invalid: {ex.Message}");
        }
    }

    private static GoalTerminalReconciliationEvidence Invalid(string metadataPath, string detail)
    {
        var fileName = Path.GetFileName(metadataPath);
        var attemptId = fileName.EndsWith(".attempt.json", StringComparison.OrdinalIgnoreCase)
            ? fileName[..^".attempt.json".Length]
            : Path.GetFileNameWithoutExtension(fileName);
        var prefix = Path.Combine(Path.GetDirectoryName(metadataPath) ?? string.Empty, attemptId);
        return Evidence(
            attemptId,
            GoalTerminalReconciliationEvidenceState.Invalid,
            metadataPath,
            prefix + ".result.json",
            prefix + ".exit.txt",
            null,
            null,
            null,
            detail);
    }

    private static GoalTerminalReconciliationEvidence Evidence(
        string attemptId,
        GoalTerminalReconciliationEvidenceState state,
        string metadataPath,
        string resultPath,
        string exitPath,
        string? typedKind,
        bool? acceptancePassed,
        int? rawExitCode,
        string detail) =>
        new(
            attemptId,
            state,
            metadataPath,
            resultPath,
            exitPath,
            typedKind,
            acceptancePassed,
            rawExitCode,
            detail);

    private static bool TryGetString(JsonElement element, string propertyName, out string value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (property.Name.Equals(propertyName, StringComparison.OrdinalIgnoreCase) &&
                property.Value.ValueKind == JsonValueKind.String)
            {
                value = property.Value.GetString() ?? string.Empty;
                return !string.IsNullOrWhiteSpace(value);
            }
        }

        value = string.Empty;
        return false;
    }

    private static bool? TryGetNestedBoolean(JsonElement element, string objectName, string propertyName)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (!property.Name.Equals(objectName, StringComparison.OrdinalIgnoreCase) ||
                property.Value.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            foreach (var nested in property.Value.EnumerateObject())
            {
                if (nested.Name.Equals(propertyName, StringComparison.OrdinalIgnoreCase) &&
                    nested.Value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                {
                    return nested.Value.GetBoolean();
                }
            }
        }

        return null;
    }
}
