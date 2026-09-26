using System.IO.Compression;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static partial class GoalOperationJournal
{
    private static IEnumerable<string> ReadJournalLines(string path)
    {
        if (!path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase))
            return Mcg.AgentOrchestrator.Core.SharedJsonlFile.ReadAllLines(path);

        return ReadCompressedLines(path);
    }

    private static IReadOnlyList<string> ReadCompressedLines(string path)
    {
        using var input = File.OpenRead(path);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var reader = new StreamReader(gzip);
        var lines = new List<string>();
        while (reader.ReadLine() is { } line) lines.Add(line);
        return lines;
    }
}
