namespace Mcg.AgentOrchestrator.Infrastructure;

/// <summary>Owner declarations use literal space separation, never shell parsing.</summary>
internal static class DeclaredCommandLine
{
    internal static string[] Parse(string value)
    {
        var tokens = value.Trim(' ').Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0 || string.IsNullOrWhiteSpace(tokens[0]) || value.IndexOfAny("|&;<>()$`'\"".ToCharArray()) >= 0)
            throw new ArgumentException("A declared command must be nonempty and contain no shell operators, backticks or quotes.");
        return tokens;
    }

    internal static string[] ParseReports(string value)
    {
        var reports = value.Split(',').Select(entry => entry.Trim().Replace('\\', '/')).ToArray();
        if (reports.Length == 0 || reports.Any(report => report == "none" || !JunitCommandCheckDefinition.IsRelativePath(report)))
            throw new ArgumentException("Reports require nonempty comma-separated relative globs without '..' segments.");
        return reports;
    }
}
