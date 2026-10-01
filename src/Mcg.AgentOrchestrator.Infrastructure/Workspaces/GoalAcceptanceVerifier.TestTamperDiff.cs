using System.Text;

namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed partial class GoalAcceptanceVerifier
{
    internal static string FilterTestFileDiffSections(string diff)
    {
        var filtered = new StringBuilder();
        var section = new List<string>();

        void FlushSection()
        {
            if (section.Count > 0 && IsTestDiffSection(section))
                filtered.Append(string.Concat(section));
            section.Clear();
        }

        // Retain each section's original line endings and bytes for the existing analyzer.
        var lines = diff.Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            if (line.StartsWith("diff --git ", StringComparison.Ordinal))
                FlushSection();
            if (section.Count > 0 || line.StartsWith("diff --git ", StringComparison.Ordinal))
                section.Add(index < lines.Length - 1 ? line + "\n" : line);
        }
        FlushSection();
        return filtered.ToString();
    }

    private static bool IsTestDiffSection(IReadOnlyList<string> section)
    {
        string? before = null, after = null;
        foreach (var rawLine in section)
        {
            var line = rawLine.TrimEnd('\r', '\n');
            if (line.StartsWith("@@", StringComparison.Ordinal)) break;
            if (line.StartsWith("--- ", StringComparison.Ordinal))
                before = DecodeDiffSidePath(line[4..], "a/");
            else if (line.StartsWith("+++ ", StringComparison.Ordinal))
                after = DecodeDiffSidePath(line[4..], "b/");
            else if (line.StartsWith("rename from ", StringComparison.Ordinal))
                before ??= DecodeGitDiffPath(line[12..]);
            else if (line.StartsWith("rename to ", StringComparison.Ordinal))
                after ??= DecodeGitDiffPath(line[10..]);
        }

        if (before is null && after is null)
            (before, after) = DecodeDiffHeaderPaths(section[0].TrimEnd('\r', '\n')[11..]);

        // A rename out of Tests still includes the old test side, as the old pathspec diff did.
        return (after is not null && IsTestFile(after)) || (before is not null && IsTestFile(before));
    }

    private static string? DecodeDiffSidePath(string value, string prefix)
    {
        var path = DecodeGitDiffPath(value);
        return path.StartsWith(prefix, StringComparison.Ordinal) ? path[prefix.Length..] : null;
    }

    private static (string? Before, string? After) DecodeDiffHeaderPaths(string header)
    {
        int split;
        if (header.StartsWith('"'))
        {
            split = 1;
            while (split < header.Length)
            {
                if (header[split] == '\\') split++;
                else if (header[split] == '"') { split++; break; }
                split++;
            }
        }
        else
        {
            // Unquoted git paths can contain spaces. Identical sides have a fixed midpoint.
            split = (header.Length - 1) / 2;
            if (split >= header.Length || header[split] != ' ' ||
                !header[(split + 1)..].StartsWith("b/", StringComparison.Ordinal))
                split = header.LastIndexOf(" b/", StringComparison.Ordinal);
        }
        if (split <= 0 || split >= header.Length || header[split] != ' ')
            return (null, null);
        return (DecodeDiffSidePath(header[..split], "a/"), DecodeDiffSidePath(header[(split + 1)..], "b/"));
    }

    private static string DecodeGitDiffPath(string path)
    {
        if (path.Length < 2 || path[0] != '"' || path[^1] != '"') return path;
        var decoded = new StringBuilder();
        for (var index = 1; index < path.Length - 1; index++)
        {
            var character = path[index];
            if (character != '\\') { decoded.Append(character); continue; }
            character = path[++index];
            if (character is >= '0' and <= '7')
            {
                var bytes = new List<byte>();
                while (true)
                {
                    var value = character - '0';
                    for (var digit = 1; digit < 3 && index + 1 < path.Length - 1 &&
                         path[index + 1] is >= '0' and <= '7'; digit++)
                        value = value * 8 + path[++index] - '0';
                    bytes.Add((byte)value);
                    if (index + 2 >= path.Length - 1 || path[index + 1] != '\\' ||
                        path[index + 2] is < '0' or > '7') break;
                    index += 2;
                    character = path[index];
                }
                decoded.Append(Encoding.UTF8.GetString(bytes.ToArray()));
            }
            else
                decoded.Append(character switch
                {
                    'a' => '\a', 'b' => '\b', 't' => '\t', 'n' => '\n',
                    'v' => '\v', 'f' => '\f', 'r' => '\r', _ => character
                });
        }
        return decoded.ToString();
    }
}
