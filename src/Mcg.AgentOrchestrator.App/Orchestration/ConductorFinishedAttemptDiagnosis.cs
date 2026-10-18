using System.Text.Json;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// Read-only context for a finished attempt; never decides whether to raise or reconcile it.
internal static class ConductorFinishedAttemptDiagnosis
{
    internal static (string Settled, string Phase) ReadHolder(string metadataPath) =>
        (ReadField(metadataPath + ".holder-settled.json", "Settled"),
         ReadField(metadataPath + ".holder.json", "Phase"));

    internal static string Describe(string attemptId, DateTimeOffset finishedAt, string metadataPath,
        IReadOnlyList<ConductorGoalLine> lines)
    {
        var holder = ReadHolder(metadataPath);
        var since = lines.Where(line => line.At > finishedAt).ToArray();
        var held = since.Count(line => line.Result == "held" && line.Reason.Contains(attemptId, StringComparison.Ordinal));
        var last = lines.Count == 0 ? "none" : lines.Max(line => line.At).ToString("O");
        return $"holderSettled:{holder.Settled},holderPhase:{holder.Phase},lastGoalLineAt:{last}," +
            $"goalLinesSinceFinish:{since.Length},heldOnAttemptSinceFinish:{held}";
    }

    internal static void Retain(Dictionary<string, List<ConductorGoalLine>> lines, string goalId, ConductorGoalLine line)
    {
        if (!lines.TryGetValue(goalId, out var retained)) lines[goalId] = retained = [];
        retained.Add(line);
        if (retained.Count > 32) retained.RemoveAt(0);
    }

    private static string ReadField(string path, string name)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            using var document = JsonDocument.Parse(reader.ReadToEnd());
            if (document.RootElement.ValueKind != JsonValueKind.Object) return "unreadable";
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
                var value = property.Value;
                if (name == "Settled")
                    return value.ValueKind switch
                    {
                        JsonValueKind.True => "true",
                        JsonValueKind.False => "false",
                        _ => "unreadable"
                    };
                if (value.ValueKind != JsonValueKind.String) return "unreadable";
                var phase = value.GetString()!;
                return phase.Length > 0 && !phase.Any(c => char.IsWhiteSpace(c) || c is ',' or ':')
                    ? phase : "unreadable";
            }
            return "unreadable";
        }
        catch (FileNotFoundException) { return "none"; }
        catch (DirectoryNotFoundException) { return "none"; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return "unreadable"; }
    }
}

internal sealed record ConductorGoalLine(DateTimeOffset At, string Result, string Reason);
