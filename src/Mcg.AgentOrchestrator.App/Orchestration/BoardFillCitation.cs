using System.Globalization;
using System.Text.RegularExpressions;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// A citation retains numeric text until bounds are known, including numbers too large for Int32.
internal sealed record BoardFillCitation(string Path, string? Start, string? End, bool IsContinuation)
{
    private static readonly Regex Suffix = new(@":([0-9]+)(?:-([0-9]+))?$", RegexOptions.CultureInvariant);

    internal bool HasLineSuffix => Start is not null;

    internal static BoardFillCitation Parse(string text)
    {
        var suffix = Suffix.Match(text);
        var path = suffix.Success ? text[..suffix.Index] : text;
        var continuation = suffix.Success && path.Length == 0;
        if (path.StartsWith("./", StringComparison.Ordinal)) path = path[2..];
        return new(path, suffix.Success ? suffix.Groups[1].Value : null,
            suffix.Groups[2].Success ? suffix.Groups[2].Value : null, continuation);
    }

    internal bool InRange(int count) =>
        int.TryParse(Start, NumberStyles.None, CultureInfo.InvariantCulture, out var start) && start >= 1 &&
        int.TryParse(End ?? Start, NumberStyles.None, CultureInfo.InvariantCulture, out var end) &&
        start <= end && end <= count;
}
