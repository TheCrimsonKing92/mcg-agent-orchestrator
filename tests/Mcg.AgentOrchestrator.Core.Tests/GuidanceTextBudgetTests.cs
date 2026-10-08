using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.Core;
using Xunit;

// Parallel-safe: reads only the explicitly resolved repository and constructs immutable guidance text.
public sealed class GuidanceTextBudgetTests
{
    private sealed record GuidanceBudget(string Name, int Ceiling, Func<string> Text);

    private static readonly GuidanceBudget[] Budgets =
    [
        // guidance-budget-table:begin
        // Planner Auto requirements are loaded in every Planner brief with automatic complexity.
        new("requirements/Planner/Auto", 1893, () => Requirements(AgentRole.Planner, TaskComplexity.Auto)),
        // Planner Simple requirements are loaded in every simple Planner brief.
        new("requirements/Planner/Simple", 1893, () => Requirements(AgentRole.Planner, TaskComplexity.Simple)),
        // Planner Complex requirements are loaded in every complex Planner brief.
        new("requirements/Planner/Complex", 2119, () => Requirements(AgentRole.Planner, TaskComplexity.Complex)),
        // Ideation Auto requirements are loaded in every Ideation brief with automatic complexity.
        new("requirements/Ideation/Auto", 304, () => Requirements(AgentRole.Ideation, TaskComplexity.Auto)),
        // Ideation Simple requirements are loaded in every simple Ideation brief.
        new("requirements/Ideation/Simple", 304, () => Requirements(AgentRole.Ideation, TaskComplexity.Simple)),
        // Ideation Complex requirements are loaded in every complex Ideation brief.
        new("requirements/Ideation/Complex", 529, () => Requirements(AgentRole.Ideation, TaskComplexity.Complex)),
        // Researcher Auto requirements are loaded in every Researcher brief with automatic complexity.
        new("requirements/Researcher/Auto", 1370, () => Requirements(AgentRole.Researcher, TaskComplexity.Auto)),
        // Researcher Simple requirements are loaded in every simple Researcher brief.
        new("requirements/Researcher/Simple", 1370, () => Requirements(AgentRole.Researcher, TaskComplexity.Simple)),
        // Researcher Complex requirements are loaded in every complex Researcher brief.
        new("requirements/Researcher/Complex", 1949, () => Requirements(AgentRole.Researcher, TaskComplexity.Complex)),
        // Developer Auto requirements add the evidence-bound premise-invalid stop instruction (120 chars).
        new("requirements/Developer/Auto", 1083, () => Requirements(AgentRole.Developer, TaskComplexity.Auto)),
        // Developer Simple requirements add the evidence-bound premise-invalid stop instruction (120 chars).
        new("requirements/Developer/Simple", 1083, () => Requirements(AgentRole.Developer, TaskComplexity.Simple)),
        // Developer Complex requirements add the evidence-bound premise-invalid stop instruction (120 chars).
        new("requirements/Developer/Complex", 1136, () => Requirements(AgentRole.Developer, TaskComplexity.Complex)),
        // Tester Auto requirements add the evidence-bound premise-invalid stop instruction (120 chars).
        new("requirements/Tester/Auto", 3521, () => Requirements(AgentRole.Tester, TaskComplexity.Auto)),
        // Tester Simple requirements add the evidence-bound premise-invalid stop instruction (120 chars).
        new("requirements/Tester/Simple", 3521, () => Requirements(AgentRole.Tester, TaskComplexity.Simple)),
        // Tester Complex requirements add the evidence-bound premise-invalid stop instruction (120 chars).
        new("requirements/Tester/Complex", 5673, () => Requirements(AgentRole.Tester, TaskComplexity.Complex)),
        // Reviewer Auto requirements are loaded in every Reviewer brief with automatic complexity.
        new("requirements/Reviewer/Auto", 4601, () => Requirements(AgentRole.Reviewer, TaskComplexity.Auto)),
        // Reviewer Simple requirements are loaded in every simple Reviewer brief.
        new("requirements/Reviewer/Simple", 4601, () => Requirements(AgentRole.Reviewer, TaskComplexity.Simple)),
        // Reviewer Complex requirements are loaded in every complex Reviewer brief.
        new("requirements/Reviewer/Complex", 5671, () => Requirements(AgentRole.Reviewer, TaskComplexity.Complex)),
        // The inline Planner directive is loaded with the result contract in every Planner brief.
        new("Planner directive", 3876, () => AgentOutputDirectives.WorkerResultTemplateLinesForRole(AgentRole.Planner)[0]),
        // Planner standing rules are loaded inline or through the Planner context artifact.
        new("PlannerStandingRules", 289, () => AgentOutputDirectives.PlannerStandingRules),
        // Developer standing rules are loaded inline or through rule.md in every Developer brief.
        new("Developer standing rules", 638, () => string.Join("\n", WorkerStandingRules.RenderBriefSection(AgentRole.Developer))),
        // AGENTS.md is copied into every worker's context as repository guidance.
        new("AGENTS.md", 34624, () => ReadRepositoryFile("AGENTS.md")),
        // CLAUDE.md is automatically loaded by the Claude harness as repository guidance.
        new("CLAUDE.md", 7355, () => ReadRepositoryFile("CLAUDE.md")),
        // guidance-budget-table:end
    ];

    [Fact]
    public void GuidanceTexts_CurrentContent_StaysWithinLiteralCeilings()
    {
        var violations = Budgets.Select(row => Violation(row.Name, row.Text(), row.Ceiling))
            .Where(message => message is not null).ToArray();

        Assert.True(violations.Length == 0, string.Join("\n", violations));
    }

    [Fact]
    public void GuidanceBudget_OverCeiling_ReportsNameCountsAndRemedy()
    {
        Assert.Equal(
            "synthetic guidance: actual 11 characters exceeds ceiling 10; remove or merge guidance, or raise this row with a written reason above it.",
            Violation("synthetic guidance", new string('x', 11), 10));
    }

    [Fact]
    public void GuidanceBudget_AtOrBelowCeiling_AcceptsNormalizedLineEndings()
    {
        Assert.Null(Violation("synthetic guidance", "x\r\ny", 3));
        Assert.Null(Violation("synthetic guidance", "x\ny", 3));
        Assert.Null(Violation("synthetic guidance", "x\r\ny", 4));
        Assert.Null(Violation("synthetic guidance", string.Empty, 0));
    }

    [Fact]
    public void GuidanceTable_AllSupportedRoleComplexities_HaveNamedRows()
    {
        Assert.Equal(Budgets.Length, Budgets.Select(row => row.Name).Distinct().Count());
        foreach (var role in Enum.GetValues<AgentRole>())
        foreach (var complexity in Enum.GetValues<TaskComplexity>())
        {
            var text = Requirements(role, complexity);
            if (text.Length == 0)
                continue;

            var row = Assert.Single(Budgets, row => row.Name == $"requirements/{role}/{complexity}");
            Assert.Equal(text, row.Text());
        }
    }

    [Fact]
    public void GuidanceTable_Source_RequiresIntegerLiteralsAndReasonComments()
    {
        var names = ReadLiteralRows(ReadRepositoryFile(
            "tests/Mcg.AgentOrchestrator.Core.Tests/GuidanceTextBudgetTests.cs"));

        Assert.Equal(Budgets.Select(row => row.Name), names);
    }

    [Fact]
    public void GuidanceTable_ExpressionCeiling_IsRejected()
    {
        const string source = """
            // guidance-budget-table:begin
            // Synthetic guidance is always loaded.
            new("synthetic guidance", 5000 + Rule.Length, () => Rule),
            // guidance-budget-table:end
            """;

        var error = Assert.Throws<InvalidOperationException>(() => ReadLiteralRows(source));
        Assert.Equal("synthetic guidance: ceiling must be a plain integer literal, found '5000 + Rule.Length'.", error.Message);
    }

    [Fact]
    public void RequirementCaps_Source_PreservesValuesAsIntegerLiterals()
    {
        var source = ReadRepositoryFile("src/Mcg.AgentOrchestrator.Core/Application/SdlcRolePromptRequirements.cs");
        Assert.Matches(@"internal const int ReviewerComplexRequirementsMaxChars = 5723;", source);
        Assert.Matches(@"internal const int ReviewerCompactRequirementsMaxChars = 4632;", source);
        Assert.Matches(@"internal const int TesterCompactRequirementsMaxChars = 3882;", source);
        Assert.Equal(5723, SdlcRolePromptRequirements.ReviewerComplexRequirementsMaxChars);
        Assert.Equal(4632, SdlcRolePromptRequirements.ReviewerCompactRequirementsMaxChars);
        Assert.Equal(3882, SdlcRolePromptRequirements.TesterCompactRequirementsMaxChars);
    }

    private static string Requirements(AgentRole role, TaskComplexity complexity) =>
        SdlcRolePromptRequirements.BuildPlainText(role, complexity);

    private static string ReadRepositoryFile(string path) =>
        File.ReadAllText(Path.Combine(VerifiedRepositoryRoot.Find(), path));

    private static string? Violation(string name, string text, int ceiling)
    {
        var actual = text.Replace("\r\n", "\n", StringComparison.Ordinal).Length;
        return actual <= ceiling ? null :
            $"{name}: actual {actual} characters exceeds ceiling {ceiling}; remove or merge guidance, or raise this row with a written reason above it.";
    }

    private static IReadOnlyList<string> ReadLiteralRows(string source)
    {
        var lines = source.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var begin = Array.FindIndex(lines, line => line.Trim() == "// guidance-budget-table:begin");
        var end = Array.FindIndex(lines, line => line.Trim() == "// guidance-budget-table:end");
        if (begin < 0 || end <= begin)
            throw new InvalidOperationException("Guidance budget table markers are missing or reversed.");

        var names = new List<string>();
        for (var index = begin + 1; index < end; index++)
        {
            var line = lines[index].Trim();
            if (line.Length == 0 || line.StartsWith("//", StringComparison.Ordinal))
                continue;

            var row = Regex.Match(line, "^new\\(\"(?<name>[^\"]+)\",\\s*(?<ceiling>[^,]+),.+\\),$");
            if (!row.Success)
                throw new InvalidOperationException($"Unrecognized guidance budget row: {line}");

            var name = row.Groups["name"].Value;
            var ceiling = row.Groups["ceiling"].Value.Trim();
            if (!Regex.IsMatch(ceiling, "^[0-9]+$"))
                throw new InvalidOperationException($"{name}: ceiling must be a plain integer literal, found '{ceiling}'.");
            if (!lines[index - 1].TrimStart().StartsWith("// ", StringComparison.Ordinal))
                throw new InvalidOperationException($"{name}: put the always-loaded explanation or written reason directly above the row.");

            names.Add(name);
        }

        if (names.Count == 0)
            throw new InvalidOperationException("Guidance budget table has no rows.");
        return names;
    }
}
