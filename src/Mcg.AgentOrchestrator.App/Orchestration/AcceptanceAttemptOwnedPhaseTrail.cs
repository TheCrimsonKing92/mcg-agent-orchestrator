using System.Globalization;
using System.Text;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class AcceptanceAttemptOwnedPhaseTrail
{
    internal static string PathFor(string metadataPath)
    {
        const string suffix = ".attempt.json";
        return metadataPath.EndsWith(suffix, StringComparison.Ordinal)
            ? metadataPath[..^suffix.Length] + ".phase.log"
            : metadataPath + ".phase.log";
    }

    internal static void Mark(string metadataPath, string phase, string? detail = null)
    {
        try
        {
            using var stream = new FileStream(PathFor(metadataPath), FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            using var writer = new StreamWriter(stream, new UTF8Encoding(false));
            writer.Write($"phase={phase} utc={DateTime.UtcNow:O} pid={Environment.ProcessId}");
            if (!string.IsNullOrEmpty(detail)) writer.Write($" {detail}");
            writer.Write('\n');
        }
        catch (IOException) { } // The trail is evidence, not the attempt verdict.
        catch (UnauthorizedAccessException) { }
    }

    internal static string? ReadLastPhase(string metadataPath)
    {
        try
        {
            using var stream = new FileStream(PathFor(metadataPath), FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return LastPhaseIn(reader.ReadToEnd());
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    internal static string? LastPhaseIn(string trail)
    {
        string? last = null;
        var lines = trail.Split('\n');
        // A concurrent append can expose an incomplete final line; retain the last complete phase.
        for (var i = 0; i < lines.Length - 1; i++)
        {
            var fields = lines[i].TrimEnd('\r').Split(' ', 4);
            if (fields.Length < 3 || !fields[0].StartsWith("phase=", StringComparison.Ordinal)
                || fields[0].Length == 6 || fields[0].Any(char.IsWhiteSpace)
                || !fields[1].StartsWith("utc=", StringComparison.Ordinal)
                || !DateTime.TryParseExact(fields[1][4..], "O", CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out _)
                || !fields[2].StartsWith("pid=", StringComparison.Ordinal)
                || !int.TryParse(fields[2][4..], NumberStyles.None, CultureInfo.InvariantCulture, out _))
                continue;
            last = fields[0][6..];
        }
        return last;
    }
}
