namespace Mcg.AgentOrchestrator.Core;

internal static class AcceptanceManifestFindingPrefix
{
    private const string FileName = "acceptance-manifest.json";

    internal static bool Matches(string line)
    {
        var separator = line.IndexOf(": ", StringComparison.Ordinal);
        if (separator < 0) return false;
        var path = line[..separator];
        if (path.Contains('\r') || path.Contains('\n') || !path.EndsWith(FileName, StringComparison.Ordinal))
            return false;
        var fileNameStart = path.Length - FileName.Length;
        return fileNameStart == 0 || path[fileNameStart - 1] is '/' or '\\';
    }
}
