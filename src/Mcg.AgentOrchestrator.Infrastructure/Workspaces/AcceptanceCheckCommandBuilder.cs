using System.Text.RegularExpressions;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class AcceptanceCheckCommandBuilder
{
    internal static bool IsDotnetCommand(string[] arguments) =>
        arguments.Length > 0 && arguments[0].Equals("dotnet", StringComparison.OrdinalIgnoreCase);

    internal static bool IsDotnetTestCommand(string[] arguments) =>
        arguments.Length >= 2 &&
        arguments[0].Equals("dotnet", StringComparison.OrdinalIgnoreCase) &&
        arguments[1].Equals("test", StringComparison.OrdinalIgnoreCase);

    internal static string[] UseDotnetHostForManagedExecutable(IReadOnlyList<string> arguments) =>
        arguments.Count > 0 && arguments[0].EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
            ? ["dotnet", .. arguments]
            : [.. arguments];

    internal static IEnumerable<string> TranslateMtpFilter(string filter)
    {
        foreach (var rawToken in Regex.Split(filter, @"[&|]"))
        {
            // A focused filter can be a parenthesized disjunction such as
            // "(FullyQualifiedName~A|FullyQualifiedName~B)|(FullyQualifiedName~C&Category!=X)".
            // MTP unions repeated --filter-class and intersects --filter-not-class/--filter-not-trait
            // exclusions, so the grouping parens carry no additional meaning at the token level and
            // are stripped before matching. (A per-group Category!= therefore widens to the whole
            // union, which is harmless because such traits are unique to a single mapped class.)
            var token = rawToken.Trim().Trim('(', ')').Trim();
            if (token.Length == 0)
            {
                continue;
            }

            var fullyQualifiedName = Regex.Match(
                token,
                @"^FullyQualifiedName\s*(?<op>!~|~)\s*(?<value>[A-Za-z_][A-Za-z0-9_.]*)$",
                RegexOptions.IgnoreCase);
            if (fullyQualifiedName.Success)
            {
                yield return fullyQualifiedName.Groups["op"].Value == "!~"
                    ? "--filter-not-class"
                    : "--filter-class";
                yield return $"*{fullyQualifiedName.Groups["value"].Value}*";
                continue;
            }

            var categoryExclusion = Regex.Match(
                token,
                @"^Category\s*!=\s*(?<value>[A-Za-z_][A-Za-z0-9_.-]*)$",
                RegexOptions.IgnoreCase);
            if (categoryExclusion.Success)
            {
                yield return "--filter-not-trait";
                yield return $"Category={categoryExclusion.Groups["value"].Value}";
                continue;
            }

            throw new InvalidOperationException($"MTP test filter '{filter}' contains unsupported token '{token}'.");
        }
    }

    internal static string[] WithBuildEnvironmentArguments(
        string[] arguments,
        DotnetBuildEnvironment environment)
    {
        // The build-environment arguments are MSBuild-shaped (--artifacts-path, -maxcpucount,
        // -p:BuildInParallel=false) and only mean something to dotnet-driven commands. Appending them to a
        // direct MTP test-executable invocation corrupts the run: every post-landing gate produced an empty
        // <TestRun /> core-tests receipt and failed structural coverage (backlog 8af9b957, 2026-07-25).
        if (arguments.Length == 0 ||
            !arguments[0].Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            return arguments;
        }

        return [.. arguments, .. environment.Arguments];
    }
}
