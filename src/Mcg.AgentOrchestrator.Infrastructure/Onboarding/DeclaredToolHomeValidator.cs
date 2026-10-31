using System.Text.RegularExpressions;

namespace Mcg.AgentOrchestrator.Infrastructure;

public static class DeclaredToolHomeValidator
{
    public const string KeyPrefix = "environment/tool-home/";
    private static readonly Regex VariablePattern = new(@"\A[A-Z][A-Z0-9_]*\z",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    public static bool IsToolHomeKey(string key) => key.StartsWith(KeyPrefix, StringComparison.Ordinal);

    public static void ValidateVariable(string variable)
    {
        if (string.IsNullOrEmpty(variable) || !VariablePattern.IsMatch(variable))
            throw new ArgumentException($"Tool-home variable '{variable}' must match ^[A-Z][A-Z0-9_]*$.");
        if (AcceptanceHermeticEnvironment.IsHermeticVerificationEnvironmentVariable(variable) ||
            variable.StartsWith("GIT_", StringComparison.Ordinal) ||
            variable.StartsWith("MCG_", StringComparison.Ordinal) ||
            WorkerProviderHomeEnvironment.Names.Contains(variable))
            throw new ArgumentException($"Tool-home variable '{variable}' is reserved by the gate or worker environment.");
    }

    public static string Validate(string variable, string directory)
    {
        ValidateVariable(variable);
        if (!Path.IsPathFullyQualified(directory) || !Directory.Exists(directory))
            throw new ArgumentException($"Tool-home variable '{variable}' requires an existing absolute directory: '{directory}'.");
        return directory;
    }

    public static IReadOnlyList<DeclaredToolHome> FromAnswers(AppliedOwnerAnswers? answers) =>
        answers?.AnsweredFacts.Where(fact => IsToolHomeKey(fact.FactKey))
            .GroupBy(fact => fact.FactKey, StringComparer.Ordinal)
            .Select(group => group.Last())
            .Select(fact => new DeclaredToolHome(fact.FactKey[KeyPrefix.Length..], fact.Fact.Value))
            .OrderBy(home => home.Variable, StringComparer.Ordinal).ToArray() ?? [];
}
