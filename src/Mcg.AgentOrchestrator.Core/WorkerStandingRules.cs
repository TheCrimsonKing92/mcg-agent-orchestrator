namespace Mcg.AgentOrchestrator.Core;

public static class WorkerStandingRules
{
    public const string Heading = "## Standing Rules";
    public const string ContextFileName = "rule.md";
    public const string PlannerContextFileName = "planner-standing-rules.md";

    private static readonly IReadOnlyList<string> DeveloperRules = Array.AsReadOnly<string>(
    [
        "Name each new test file after its public test class.",
        "A new test class in a non-parallel xunit collection must contain that collection's acceptance-lane substring in its class name.",
        "Tests never decide pass or fail on wall-clock time; bounded waits are hang-only and name the event that did not happen.",
        "Do not run dotnet test directly; report the test classes to run as `tests: deferred - ClassA, ClassB`.",
        "Before changing any message, reason or diagnostic text, search the tests for exact-equality assertions on it, and if one exists put new information in a separate field instead of editing the asserted text."
    ]);

    private static readonly IReadOnlyList<string> GoDeveloperRules = Array.AsReadOnly<string>(
    [
        "Name each new test file `<name>_test.go` and keep it next to the code it tests.",
        "Tests never decide pass or fail on wall-clock time; bounded waits are hang-only and name the event that did not happen.",
        "Run `go build ./...`, `go vet ./...` and `go test ./...` yourself and report each result in `tests`.",
        "Before changing any message, reason or diagnostic text, search the tests for exact-equality assertions on it, and if one exists put new information in a separate field instead of editing the asserted text.",
        "Do not reformat Go files you did not change, and do not run `go mod tidy` unless the task asks."
    ]);

    public static IReadOnlyList<string> RulesForRole(AgentRole role, ProjectStack stack = ProjectStack.Dotnet) => (role, stack) switch
    {
        (AgentRole.Developer, ProjectStack.Go) => GoDeveloperRules,
        (AgentRole.Tester, ProjectStack.Go) => GoDeveloperRules.Take(3).ToArray(),
        (AgentRole.Developer, _) => DeveloperRules,
        (AgentRole.Tester, _) => DeveloperRules.Take(3).ToArray(),
        _ => []
    };

    public static string? ContextFileNameForRole(AgentRole role) => role switch
    {
        AgentRole.Developer or AgentRole.Tester => ContextFileName,
        AgentRole.Planner => PlannerContextFileName,
        _ => null
    };

    // The brief already identifies the context directory; keep this file pointer compact.
    public static string ContextReference(AgentRole role) =>
        ContextFileNameForRole(role) ?? throw new ArgumentOutOfRangeException(nameof(role), role, "Role has no standing-rules context file.");

    public static void WriteContextArtifact(AgentRole role, string contextDirectory, ProjectStack stack = ProjectStack.Dotnet)
    {
        if (ContextFileNameForRole(role) is not { } fileName)
        {
            return;
        }

        var lines = role == AgentRole.Planner
            ? new[] { "## Planner Standing Rules", AgentOutputDirectives.PlannerStandingRules }
            : RenderBriefSection(role, stack: stack);
        Directory.CreateDirectory(contextDirectory);
        File.WriteAllText(Path.Combine(contextDirectory, fileName), string.Join(Environment.NewLine, lines));
    }

    public static IReadOnlyList<string> RenderBriefSection(AgentRole role, string? contextDirectory = null, ProjectStack stack = ProjectStack.Dotnet)
    {
        var rules = RulesForRole(role, stack);
        if (rules.Count == 0)
        {
            return [];
        }

        if (!string.IsNullOrWhiteSpace(contextDirectory))
        {
            return [ContextReference(role)];
        }

        return [Heading, .. rules.Select(rule => $"- {rule}"), string.Empty];
    }
}
