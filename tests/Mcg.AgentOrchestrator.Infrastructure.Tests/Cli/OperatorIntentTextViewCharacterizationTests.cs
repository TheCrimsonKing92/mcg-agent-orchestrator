using System.Reflection;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Xunit;

// Parallel-safe: AsyncLocal console capture and immutable, local fixtures.
public sealed class OperatorIntentTextViewCharacterizationTests
{
    [Fact]
    public void Templates_TwoEntries_PreservesExactText()
    {
        OperatorIntentTemplate[] templates =
        [
            new("feature", "Build a feature.", [], [], [], []),
            new("repair", "Repair a defect.", [], [], [], [])
        ];

        var actual = AsyncLocalConsoleRouter.Capture(() =>
            OperatorIntentTextView.PrintOperatorIntentTemplates(templates));

        Assert.Equal(string.Join(Environment.NewLine,
            "",
            "Intent templates: 2",
            "  feature: Build a feature.",
            "  repair: Repair a defect.",
            "",
            "Create commands:",
            "  intent-template <template> <request> --create-goal",
            "  intent-template <template> <request> --create-simple-goal",
            "",
            ""), actual);
    }

    [Fact]
    public void Templates_Empty_PreservesExactText()
    {
        var actual = AsyncLocalConsoleRouter.Capture(() =>
            OperatorIntentTextView.PrintOperatorIntentTemplates([]));

        Assert.Equal(string.Join(Environment.NewLine,
            "",
            "Intent templates: 0",
            "",
            "Create commands:",
            "  intent-template <template> <request> --create-goal",
            "  intent-template <template> <request> --create-simple-goal",
            "",
            ""), actual);
    }

    [Fact]
    public void Plan_ReadyObjective_PreservesExactText()
    {
        var template = new OperatorIntentTemplate("feature", "Build a feature.", [], [], [], []);
        var plan = new OperatorIntentPlan(template, "Add a command.", "Implement the command.");

        var actual = AsyncLocalConsoleRouter.Capture(() =>
            OperatorIntentTextView.PrintOperatorIntentPlan(plan));

        Assert.Equal(string.Join(Environment.NewLine,
            "",
            "Intent template: feature",
            "Build a feature.",
            "",
            "Implement the command.",
            "",
            "Create commands:",
            "  intent-template feature <request> --create-goal",
            "  intent-template feature <request> --create-simple-goal",
            "",
            ""), actual);
    }

    [Fact]
    public void ExtractedOwner_LeavesNoPartialFileOrConsoleViewsForwarders()
    {
        var cliRoot = Path.Combine(VerifiedRepositoryRoot.Find(), "src", "Mcg.AgentOrchestrator.App", "Cli");
        Assert.False(File.Exists(Path.Combine(cliRoot, "ConsoleViews.OperatorIntent.cs")));
        var source = File.ReadAllText(Path.Combine(cliRoot, "OperatorIntentTextView.cs"));
        Assert.Contains("internal static class OperatorIntentTextView", source);
        Assert.DoesNotMatch(@"\bpartial\s+class\b", source);
        var methods = typeof(ConsoleViews).GetMethods(
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly);
        Assert.DoesNotContain(methods, method =>
            method.Name is "PrintOperatorIntentTemplates" or "PrintOperatorIntentPlan");
    }
}
