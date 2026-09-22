using Mcg.AgentOrchestrator.App.Cli;
using System.Text.RegularExpressions;

public sealed class CliAcceptanceStableSlotFamilyTests
{
    private const string InvocationMarker = "CliPersistentStateRunner.ExecuteCommand(";
    private static readonly Regex LiteralCommandInvocation = new(
        Regex.Escape(InvocationMarker) + "\\s*\\[\\s*\"(?<command>[^\"]+)\"",
        RegexOptions.CultureInvariant);

    [Xunit.Fact]
    public void AcceptanceCommandDriversSupplyAnIsolatedStableSlot()
    {
        var repositoryRoot = InfrastructureTestSupport.FindRepositoryRoot();
        var testRoot = Path.Combine(repositoryRoot, "tests", "Mcg.AgentOrchestrator.Infrastructure.Tests");
        var violations = Directory
            .EnumerateFiles(testRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path => !IsGeneratedSource(path))
            .SelectMany(path => FindUnpinnedAcceptanceInvocations(
                File.ReadAllText(path),
                Path.GetRelativePath(repositoryRoot, path).Replace('\\', '/')))
            .ToArray();

        Xunit.Assert.True(
            violations.Length == 0,
            "CLI acceptance commands must use AcceptanceStableSlotTestSupport or pass stableSlotSelector:" +
            Environment.NewLine + string.Join(Environment.NewLine, violations));
        var supportSource = File.ReadAllText(Path.Combine(testRoot, "AcceptanceStableSlotTestSupport.cs"));
        Xunit.Assert.Contains("stableSlotSelector:", supportSource, StringComparison.Ordinal);
    }

    [Xunit.Theory]
    [Xunit.InlineData("acceptance", false, true)]
    [Xunit.InlineData("accept", false, true)]
    [Xunit.InlineData("acceptance-queue", false, true)]
    [Xunit.InlineData("acceptance", true, false)]
    [Xunit.InlineData("acceptance-retry", false, false)]
    [Xunit.InlineData("conduct", false, false)]
    public void DetectorClassifiesOnlyUnpinnedStableSlotAcquiringCommands(
        string command,
        bool suppliesSelector,
        bool expectsViolation)
    {
        var directCall = "CliPersistentStateRunner" +
            $".ExecuteCommand([\"{command}\"], repository" +
            (suppliesSelector ? ", stableSlotSelector: selector);" : ");");

        var violations = FindUnpinnedAcceptanceInvocations(directCall, "synthetic.cs");

        Xunit.Assert.Equal(expectsViolation, violations.Count == 1);
    }

    private static IReadOnlyList<string> FindUnpinnedAcceptanceInvocations(string source, string path)
    {
        var violations = new List<string>();
        foreach (Match match in LiteralCommandInvocation.Matches(source))
        {
            var command = match.Groups["command"].Value;
            if (!CliPersistentStateRunner.IsAcceptanceCommand([command]))
                continue;

            var openParenthesis = match.Index + InvocationMarker.Length - 1;
            var closeParenthesis = FindClosingParenthesis(source, openParenthesis);
            if (closeParenthesis < 0)
            {
                violations.Add($"{path}:{LineNumber(source, match.Index)} malformed ExecuteCommand invocation");
                continue;
            }

            var invocation = source[openParenthesis..(closeParenthesis + 1)];
            if (!invocation.Contains("stableSlotSelector:", StringComparison.Ordinal))
                violations.Add($"{path}:{LineNumber(source, match.Index)} command={command}");
        }

        return violations;
    }

    private static int FindClosingParenthesis(string source, int openParenthesis)
    {
        var depth = 0;
        var quote = '\0';
        var escaped = false;
        for (var index = openParenthesis; index < source.Length; index++)
        {
            var current = source[index];
            if (quote != '\0')
            {
                if (escaped)
                {
                    escaped = false;
                    continue;
                }

                if (current == '\\' && quote == '"')
                {
                    escaped = true;
                    continue;
                }

                if (current == quote)
                    quote = '\0';
                continue;
            }

            if (current is '"' or '\'')
            {
                quote = current;
                continue;
            }

            if (current == '(')
                depth++;
            else if (current == ')' && --depth == 0)
                return index;
        }

        return -1;
    }

    private static bool IsGeneratedSource(string path)
        => path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) ||
           path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase);

    private static int LineNumber(string source, int index)
        => source.Take(index).Count(character => character == '\n') + 1;
}
