namespace Mcg.AgentOrchestrator.Core;

public sealed record DogfoodLogEntry(
    string Header,
    string Summary,
    string OperatorGate,
    string ModelFit)
{
    public string Render()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine(Header);
        sb.AppendLine();
        sb.AppendLine(Summary);
        sb.AppendLine();
        sb.AppendLine($"- Operator gate: {OperatorGate}");
        sb.Append($"- {ModelFit}");
        return sb.ToString();
    }
}

public static class DogfoodLogRenderer
{
    private const string NoReceipt = "(no receipt)";

    public static DogfoodLogEntry Render(Goal goal)
    {
        var date = GetGoalDate(goal);
        var title = BuildTitle(goal.Objective);
        var header = $"## {date:yyyy-MM-dd} - {title}";
        var summary = BuildSummary(goal);
        var operatorGate = BuildOperatorGate(goal);
        var modelFit = BuildModelFit(goal);
        return new DogfoodLogEntry(header, summary, operatorGate, modelFit);
    }

    private static DateTimeOffset GetGoalDate(Goal goal)
    {
        return goal.Timeline.Count > 0 ? goal.Timeline[^1].OccurredAt : DateTimeOffset.UtcNow;
    }

    private static string BuildTitle(string objective)
    {
        var normalized = objective.ReplaceLineEndings(" ").Trim();
        var end = normalized.IndexOfAny(['.', '!', '?']);
        var sentence = end > 0 ? normalized[..end].Trim() : normalized;
        return sentence.Length <= 80 ? sentence : sentence[..77] + "...";
    }

    private static string BuildSummary(Goal goal)
    {
        var prefix = goal.Id.Value[..8];
        var objectiveOneLine = goal.Objective
            .ReplaceLineEndings(" ")
            .Trim();
        if (objectiveOneLine.Length > 120)
        {
            objectiveOneLine = objectiveOneLine[..117] + "...";
        }

        var taskLines = goal.Tasks.Select(BuildTaskLine).ToList();
        var acceptanceStatus = goal.Status == GoalStatus.Completed ? "Acceptance passed."
            : goal.Status == GoalStatus.Failed ? "Acceptance failed."
            : $"Goal status: {goal.Status}.";

        return $"Goal {prefix}: {objectiveOneLine}. {string.Join(" ", taskLines)} {acceptanceStatus}";
    }

    private static string BuildTaskLine(TaskSpec task)
    {
        var role = task.RequiredRole.ToString();

        string model;
        if (task.LastDispatch is { ProviderName: { } provider, ModelName: { } modelName })
        {
            model = $"{provider}/{modelName}";
        }
        else
        {
            model = NoReceipt;
        }

        string exitInfo;
        string commit;
        if (task.LastVerification is { } verification)
        {
            exitInfo = $"exit {verification.ExitCode}";
            var sha = TryParseWorkerResultField(verification, "commit");
            commit = sha is { Length: > 0 } && !IsPlaceholder(sha) ? sha : NoReceipt;
        }
        else
        {
            exitInfo = NoReceipt;
            commit = NoReceipt;
        }

        return $"{role} task via {model} ({exitInfo}, commit {commit}).";
    }

    private static string BuildOperatorGate(Goal goal)
    {
        var parts = new List<string>();
        foreach (var task in goal.Tasks)
        {
            if (task.LastVerification is not { } verification)
                continue;

            var tests = TryParseWorkerResultField(verification, "tests");
            if (tests is { Length: > 0 } && !IsPlaceholder(tests))
            {
                parts.Add($"{task.RequiredRole}: {tests} (exit {verification.ExitCode})");
            }
        }

        return parts.Count > 0 ? string.Join("; ", parts) : NoReceipt;
    }

    private static string BuildModelFit(Goal goal)
    {
        // Prefer pre-extracted ModelFitNote (set by ModelFitEvidence.TryExtractNote on recording)
        foreach (var task in goal.Tasks.Reverse<TaskSpec>())
        {
            if (task.LastVerification?.ModelFitNote is { Length: > 0 } note)
                return note;
        }

        // Fall back to parsing raw WORKER_RESULT model_fit field
        foreach (var task in goal.Tasks.Reverse<TaskSpec>())
        {
            if (task.LastVerification is { } verification)
            {
                var raw = TryParseWorkerResultField(verification, "model_fit");
                if (raw is { Length: > 0 } && !IsPlaceholder(raw))
                    return $"Model fit: {raw}";
            }
        }

        return NoReceipt;
    }

    public static string? TryParseWorkerResultField(TaskVerificationRecord verification, string fieldName)
    {
        var combined = $"{verification.StandardOutput}\n{verification.StandardError}";
        var lines = combined.Replace("\r\n", "\n").Split('\n');
        var inBlock = false;

        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (string.Equals(trimmed, "WORKER_RESULT:", StringComparison.OrdinalIgnoreCase))
            {
                inBlock = true;
                continue;
            }

            if (string.Equals(trimmed, "END_WORKER_RESULT", StringComparison.OrdinalIgnoreCase))
            {
                if (inBlock) break;
                continue;
            }

            if (!inBlock)
                continue;

            var sep = trimmed.IndexOf(':', StringComparison.Ordinal);
            if (sep <= 0)
                continue;

            var key = trimmed[..sep].Trim();
            if (!string.Equals(key, fieldName, StringComparison.OrdinalIgnoreCase))
                continue;

            var value = trimmed[(sep + 1)..].Trim();
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }

        return null;
    }

    private static bool IsPlaceholder(string value)
    {
        // Worker emitted the template placeholder rather than real evidence
        return value.StartsWith('<') && value.EndsWith('>');
    }
}
