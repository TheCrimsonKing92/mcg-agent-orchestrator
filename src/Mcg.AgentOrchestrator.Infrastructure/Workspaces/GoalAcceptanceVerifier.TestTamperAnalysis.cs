using System.Text;
using System.Text.RegularExpressions;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class TestTamperAnalysis
{
    private static readonly Regex TestAttrPattern = new(
        @"^\s*\[\s*(?:Xunit\.)?(?:Fact|Theory)\s*(?:\(|,|\])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex TestMethodDeclarationPattern = new(
        @"\b(?:public|internal|protected|private)\s+(?:static\s+)?(?:async\s+)?(?:[\w<>,.?\[\]]+\s+)+(?<name>[A-Za-z_]\w*)\s*\(",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex DiffContainingTypePattern = new(
        @"\b(?:class|struct|record(?:\s+class|\s+struct)?)\s+(?<name>[A-Za-z_]\w*)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex TautologyPattern = new(
        @"Assert\.True\(\s*true\s*\)|Assert\.False\(\s*false\s*\)|Assert\.Equal\(\s*(?<v>\w+)\s*,\s*\k<v>\s*\)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly string[] DiffBaseArgs = ["git", "diff", "--unified=0", "main...HEAD"];

    internal static async Task<AcceptanceCheckResult> RunTestTamperCheckAsync(
        Func<string[], string, TimeSpan, CancellationToken, Task<GoalAcceptanceVerifier.CommandResult>> runner,
        AcceptanceGateEngineSettings engineSettings,
        string worktreePath,
        IReadOnlyList<string> sanctionedRemovedTests,
        CancellationToken cancellationToken)
    {
        const string CheckName = "test tamper guard";

        string[] diffArgs = [.. DiffBaseArgs];

        var result = await runner(
            diffArgs,
            worktreePath,
            engineSettings.ResolveCheckTimeout(null),
            cancellationToken).ConfigureAwait(false);

        if (result.ExitCode != 0)
            return new AcceptanceCheckResult(CheckName, true, 0, null, Advisory: true, ResultSummary: "diff unavailable");

        var signals = AnalyzeTestFileDiff(FilterTestFileDiffSections(result.Output), sanctionedRemovedTests);

        if (signals.Count == 0)
            return new AcceptanceCheckResult(CheckName, true, 0, null, Advisory: true, ResultSummary: "no test degradation detected");

        return new AcceptanceCheckResult(
            CheckName, false, 1,
            string.Join(Environment.NewLine, signals),
            Advisory: true,
            ResultSummary: $"{signals.Count} test degradation signal(s)");
    }

    internal static bool IsTestFile(string path) =>
        path.Contains("Tests", StringComparison.OrdinalIgnoreCase);

    private static List<string> AnalyzeTestFileDiff(
        string diff,
        IReadOnlyList<string> sanctionedRemovedTests)
    {
        var signals = new List<string>();
        var fileStats = new List<TestFileDiffStats>();
        string? currentFile = null;
        string? pendingFile = null;
        int assertRemoved = 0, assertAdded = 0;
        int testAttrRemoved = 0, testAttrAdded = 0;
        var sanctionedAssertRemoved = 0;
        var sanctionedTestAttrRemoved = 0;
        var pendingRemovedTestAttribute = false;
        var inSanctionedRemovedMethod = false;
        string? currentContainingType = null;
        var sanctionedMethodBraceDepth = 0;
        var sanctionedMethodBodyStarted = false;
        var tautologies = new List<string>();

        void FlushFile()
        {
            if (currentFile is null) return;

            fileStats.Add(new TestFileDiffStats(
                currentFile,
                Math.Max(0, assertRemoved - sanctionedAssertRemoved),
                assertAdded,
                Math.Max(0, testAttrRemoved - sanctionedTestAttrRemoved),
                testAttrAdded));

            foreach (var t in tautologies)
                signals.Add($"{currentFile}: tautology assertion added: {t}");
        }

        void StartFile(string filePath)
        {
            FlushFile();
            currentFile = filePath;
            assertRemoved = assertAdded = testAttrRemoved = testAttrAdded = 0;
            sanctionedAssertRemoved = sanctionedTestAttrRemoved = 0;
            pendingRemovedTestAttribute = false;
            inSanctionedRemovedMethod = false;
            currentContainingType = null;
            sanctionedMethodBraceDepth = 0;
            sanctionedMethodBodyStarted = false;
            tautologies.Clear();
        }

        foreach (var rawLine in diff.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');

            if (line.StartsWith("--- a/", StringComparison.Ordinal))
            {
                pendingFile = line[6..];
            }
            else if (line.StartsWith("+++ b/", StringComparison.Ordinal))
            {
                StartFile(line[6..]);
                pendingFile = null;
            }
            else if (line.StartsWith("+++ /dev/null", StringComparison.Ordinal) && pendingFile is not null)
            {
                StartFile(pendingFile);
                pendingFile = null;
            }
            else if (line.StartsWith("@@", StringComparison.Ordinal))
            {
                var containingTypeMatch = DiffContainingTypePattern.Match(line);
                currentContainingType = containingTypeMatch.Success
                    ? containingTypeMatch.Groups["name"].Value
                    : null;
            }
            else if (line.Length > 1 && line[0] is '-' or '+' &&
                     !line.StartsWith("--- ", StringComparison.Ordinal) &&
                     !line.StartsWith("+++ ", StringComparison.Ordinal))
            {
                var content = line[1..];
                var trimmed = content.TrimStart();

                if (line[0] == '-')
                {
                    var containingTypeMatch = DiffContainingTypePattern.Match(trimmed);
                    if (containingTypeMatch.Success)
                        currentContainingType = containingTypeMatch.Groups["name"].Value;

                    var startsSanctionedRemovedMethod = false;
                    if (trimmed.StartsWith("Assert.", StringComparison.Ordinal))
                    {
                        assertRemoved++;
                        if (inSanctionedRemovedMethod)
                            sanctionedAssertRemoved++;
                    }
                    if (TestAttrPattern.IsMatch(trimmed))
                    {
                        testAttrRemoved++;
                        pendingRemovedTestAttribute = true;
                    }

                    if (pendingRemovedTestAttribute &&
                        TryGetTestMethodName(trimmed, out var methodName))
                    {
                        inSanctionedRemovedMethod = sanctionedRemovedTests.Any(identity =>
                            DeclaredIdentityMatchesMethod(identity, currentFile!, currentContainingType, methodName));
                        startsSanctionedRemovedMethod = inSanctionedRemovedMethod;
                        if (inSanctionedRemovedMethod)
                            sanctionedTestAttrRemoved++;
                        pendingRemovedTestAttribute = false;
                    }

                    if (inSanctionedRemovedMethod)
                    {
                        var expressionBodiedMethod = startsSanctionedRemovedMethod &&
                            content.Contains("=>", StringComparison.Ordinal) &&
                            content.Contains(';', StringComparison.Ordinal);
                        var opens = content.Count(ch => ch == '{');
                        var closes = content.Count(ch => ch == '}');
                        if (opens > 0)
                            sanctionedMethodBodyStarted = true;
                        sanctionedMethodBraceDepth += opens - closes;
                        if (expressionBodiedMethod ||
                            sanctionedMethodBodyStarted && sanctionedMethodBraceDepth <= 0)
                        {
                            inSanctionedRemovedMethod = false;
                            sanctionedMethodBraceDepth = 0;
                            sanctionedMethodBodyStarted = false;
                        }
                    }
                }
                else
                {
                    if (trimmed.StartsWith("Assert.", StringComparison.Ordinal))
                        assertAdded++;
                    if (TestAttrPattern.IsMatch(trimmed))
                        testAttrAdded++;
                    if (TautologyPattern.IsMatch(content))
                        tautologies.Add(trimmed.Length > 80 ? trimmed[..80] + "..." : trimmed);
                }
            }
        }

        FlushFile();

        var totalAssertRemoved = fileStats.Sum(file => file.AssertRemoved);
        var totalAssertAdded = fileStats.Sum(file => file.AssertAdded);
        var netAssertRemoved = totalAssertRemoved - totalAssertAdded;
        if (netAssertRemoved > 0)
        {
            signals.Insert(
                0,
                $"diff-wide net -{netAssertRemoved} assertion(s) removed ({FormatTestFileDiffDetails(fileStats)})");
        }

        var totalTestAttrRemoved = fileStats.Sum(file => file.TestAttrRemoved);
        var totalTestAttrAdded = fileStats.Sum(file => file.TestAttrAdded);
        var netTestAttrRemoved = totalTestAttrRemoved - totalTestAttrAdded;
        if (netTestAttrRemoved > 0)
        {
            signals.Insert(
                netAssertRemoved > 0 ? 1 : 0,
                $"diff-wide {netTestAttrRemoved} test method(s) removed ({FormatTestFileDiffDetails(fileStats)})");
        }

        return signals;
    }

    private static bool TryGetTestMethodName(string line, out string methodName)
    {
        var match = TestMethodDeclarationPattern.Match(line);
        methodName = match.Success ? match.Groups["name"].Value : string.Empty;
        return match.Success;
    }

    private static bool DeclaredIdentityMatchesMethod(
        string identity,
        string filePath,
        string? containingType,
        string methodName)
    {
        var normalized = identity.Trim();
        var argumentsIndex = normalized.IndexOf('(');
        if (argumentsIndex >= 0)
            normalized = normalized[..argumentsIndex];

        var separatorIndex = Math.Max(normalized.LastIndexOf('.'), normalized.LastIndexOf(':'));
        var declaredMethod = separatorIndex >= 0 ? normalized[(separatorIndex + 1)..] : normalized;
        if (!declaredMethod.Equals(methodName, StringComparison.OrdinalIgnoreCase) || separatorIndex <= 0)
            return false;

        var containingIdentity = normalized[..separatorIndex].TrimEnd('.', ':');
        var containingSeparatorIndex = Math.Max(
            containingIdentity.LastIndexOf('.'),
            containingIdentity.LastIndexOf(':'));
        var declaredClass = containingSeparatorIndex >= 0
            ? containingIdentity[(containingSeparatorIndex + 1)..]
            : containingIdentity;
        return declaredClass.Equals(
            containingType ?? Path.GetFileNameWithoutExtension(filePath),
            StringComparison.OrdinalIgnoreCase);
    }

    private static string FormatTestFileDiffDetails(IReadOnlyList<TestFileDiffStats> fileStats)
    {
        var details = fileStats
            .Where(file =>
                file.AssertRemoved != 0 ||
                file.AssertAdded != 0 ||
                file.TestAttrRemoved != 0 ||
                file.TestAttrAdded != 0)
            .Select(file =>
                $"{file.FilePath}: assertions -{file.AssertRemoved}/+{file.AssertAdded}, tests -{file.TestAttrRemoved}/+{file.TestAttrAdded}");

        return "per-file: " + string.Join("; ", details);
    }

    private sealed record TestFileDiffStats(
        string FilePath,
        int AssertRemoved,
        int AssertAdded,
        int TestAttrRemoved,
        int TestAttrAdded);

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
