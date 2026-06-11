namespace Mcg.AgentOrchestrator.Core;

public sealed record ModelFitObservation(string ProviderName, string ModelName, string Fit, string? TaskShape = null);

public static class ModelFitEvidence
{
    public const string NotePrefix = "Model fit:";

    // Single source for the note format; emitters must not restate it so the
    // parser and prompts cannot drift apart.
    public static string BuildNoteTemplate(string target)
    {
        return $"{NotePrefix} {target} - adequate|overkill|underpowered - <task shape> - <short reason>";
    }

    public static string? FindLatestNote(TaskSpec task)
    {
        foreach (var verification in task.VerificationHistory.Reverse())
        {
            foreach (var line in EnumerateVerificationLines(verification))
            {
                var normalized = NormalizeNoteLine(line);
                if (normalized.StartsWith(NotePrefix, StringComparison.OrdinalIgnoreCase))
                {
                    return normalized;
                }
            }
        }

        return null;
    }

    public static IEnumerable<string> FindNotes(TaskSpec task)
    {
        foreach (var verification in task.VerificationHistory)
        {
            foreach (var line in EnumerateVerificationLines(verification))
            {
                var normalized = NormalizeNoteLine(line);
                if (normalized.StartsWith(NotePrefix, StringComparison.OrdinalIgnoreCase))
                {
                    yield return normalized;
                }
            }
        }
    }

    public static string? TryExtractNote(string stdout, string stderr)
    {
        foreach (var line in SplitLines(stdout).Concat(SplitLines(stderr)))
        {
            var normalized = NormalizeNoteLine(line);
            if (normalized.StartsWith(NotePrefix, StringComparison.OrdinalIgnoreCase))
            {
                return normalized;
            }
        }

        return null;
    }

    public static List<ModelFitSummary> BuildSummary(IEnumerable<string?> notes)
    {
        return notes
            .Select(TryParseNote)
            .Where(observation => observation is not null)
            .Cast<ModelFitObservation>()
            .GroupBy(
                observation => new
                {
                    observation.ProviderName,
                    observation.ModelName
                })
            .OrderBy(group => group.Key.ProviderName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(group => group.Key.ModelName, StringComparer.OrdinalIgnoreCase)
            .Select(group => new ModelFitSummary(
                group.Key.ProviderName,
                group.Key.ModelName,
                group.Count(),
                group.Count(observation => observation.Fit == "adequate"),
                group.Count(observation => observation.Fit == "overkill"),
                group.Count(observation => observation.Fit == "underpowered"),
                group.Count(observation => observation.Fit == "unknown"),
                BuildTaskShapes(group)))
            .ToList();
    }

    public static ModelFitObservation? TryParseNote(string? note)
    {
        if (string.IsNullOrWhiteSpace(note))
        {
            return null;
        }

        var normalized = NormalizeNoteLine(note);
        if (!normalized.StartsWith(NotePrefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var body = normalized[NotePrefix.Length..].Trim();
        var parts = body.Split([" - "], StringSplitOptions.None);
        if (parts.Length < 2)
        {
            return null;
        }

        var target = parts[0].Trim();
        var separator = target.IndexOf('/');
        if (separator <= 0 || separator >= target.Length - 1)
        {
            return null;
        }

        var providerName = target[..separator].Trim();
        var modelName = target[(separator + 1)..].Trim();
        if (string.IsNullOrWhiteSpace(providerName) || string.IsNullOrWhiteSpace(modelName))
        {
            return null;
        }

        // A literal echo of the prompt template ("adequate|overkill|underpowered")
        // is not evidence; drop it instead of counting it as an unknown fit.
        if (parts[1].Contains('|'))
        {
            return null;
        }

        var fit = parts[1].Trim().ToLowerInvariant() switch
        {
            "adequate" => "adequate",
            "overkill" => "overkill",
            "underpowered" => "underpowered",
            _ => "unknown"
        };

        var taskShape = parts.Length >= 3 ? NormalizeTaskShape(parts[2]) : null;

        return new ModelFitObservation(providerName, modelName, fit, taskShape);
    }

    private static List<string> BuildTaskShapes(IEnumerable<ModelFitObservation> observations)
    {
        return observations
            .Select(observation => observation.TaskShape)
            .Where(shape => !string.IsNullOrWhiteSpace(shape))
            .Select(shape => shape!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(shape => shape, StringComparer.OrdinalIgnoreCase)
            .Take(3)
            .ToList();
    }

    private static string? NormalizeTaskShape(string value)
    {
        var trimmed = value.Trim().TrimEnd('.');
        if (string.IsNullOrWhiteSpace(trimmed) ||
            (trimmed.StartsWith('<') && trimmed.EndsWith('>')))
        {
            return null;
        }

        return trimmed;
    }

    // Markdown-formatting workers (claude in particular) emit the note as
    // "**Model fit:** Provider/model — adequate — ..."; strip emphasis/list
    // decoration and normalize en/em dashes so the line-based parser sees it.
    private static string NormalizeNoteLine(string line)
    {
        var trimmed = line.Trim().TrimStart('#', '>', '-', '*', '_', ' ');
        return trimmed
            .Replace("**", string.Empty)
            .Replace('–', '-')
            .Replace('—', '-')
            .Trim();
    }

    private static IEnumerable<string> EnumerateVerificationLines(TaskVerificationRecord verification)
    {
        return SplitLines(verification.StandardOutput)
            .Concat(SplitLines(verification.StandardError));
    }

    private static string[] SplitLines(string value)
    {
        return value.ReplaceLineEndings("\n").Split('\n', StringSplitOptions.RemoveEmptyEntries);
    }
}
