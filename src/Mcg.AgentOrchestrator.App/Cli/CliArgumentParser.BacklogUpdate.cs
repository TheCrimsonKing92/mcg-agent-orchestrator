using System.Text.RegularExpressions;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static partial class CliArgumentParser
{
    private static readonly HashSet<string> BacklogUpdateValueFlags = new(StringComparer.OrdinalIgnoreCase)
    {
        "--title", "--description", "--priority", "--tags", "--status", "--text-file", "--body-file"
    };

    internal static bool IsBacklogUpdateValueFlag(string token) => BacklogUpdateValueFlags.Contains(token);

    internal static bool IsBacklogUpdateTerminator(string token) =>
        IsBacklogUpdateValueFlag(token) ||
        token.Equals("--help", StringComparison.OrdinalIgnoreCase) ||
        token.Equals("-h", StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyList<string> SplitBacklogUpdateCommand(string command, string remainder)
    {
        // Token offsets locate flags; values come from the original line, never rejoined words.
        var tokens = Regex.Matches(remainder, @"\S+");
        var result = new List<string> { command };
        for (var index = 0; index < tokens.Count; index++)
        {
            var token = tokens[index];
            result.Add(token.Value);
            if (index == 0 || !IsBacklogUpdateValueFlag(token.Value))
                continue;

            var next = index + 1;
            while (next < tokens.Count && !IsBacklogUpdateTerminator(tokens[next].Value))
                next++;

            if (next > index + 1)
            {
                // One whitespace character separates a flag from its value and the next flag.
                // All other whitespace, including the final trailing whitespace, is value data.
                var start = token.Index + token.Length + 1;
                var end = next < tokens.Count ? tokens[next].Index - 1 : remainder.Length;
                result.Add(remainder[start..end]);
            }
            index = next - 1;
        }
        return result;
    }

    private static string[] NormalizeBacklogUpdateArgs(string[] args)
    {
        if (args.Length <= 2)
            return args;

        var result = new List<string> { args[0], args[1] };
        for (var index = 2; index < args.Length; index++)
        {
            result.Add(args[index]);
            if (!IsBacklogUpdateValueFlag(args[index]))
                continue;

            var start = index + 1;
            var next = start;
            while (next < args.Length && !IsBacklogUpdateTerminator(args[next]))
                next++;

            if (next > start)
                result.Add(string.Join(' ', args[start..next]));
            index = next - 1;
        }
        return [.. result];
    }
}
