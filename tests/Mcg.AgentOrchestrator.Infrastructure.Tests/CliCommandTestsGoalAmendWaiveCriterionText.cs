using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.SubscriptionPlanning;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection(TestCollections.CliProcessEnvironment)]
public sealed class CliCommandTestsGoalAmendWaiveCriterionText : CliCommandTestBase
{
    [Xunit.Fact]
    public async Task WaiveSecondCriterion_PrintsResolvedTextAndExistingFields()
    {
        var output = await WaiveSecondCriterion("beta criterion two");
        var confirmation = ConfirmationLine(output);

        Xunit.Assert.StartsWith("Acceptance criterion waived: goal=", confirmation);
        Xunit.Assert.Contains("criterion=2 text=beta criterion two", confirmation);
        Xunit.Assert.EndsWith(" actor=operator reason=scope clarified", confirmation);
        Xunit.Assert.DoesNotContain("text=alpha criterion one", output);
    }

    [Xunit.Theory]
    [Xunit.InlineData(200)]
    [Xunit.InlineData(201)]
    [Xunit.InlineData(240)]
    public async Task WaiveLongCriterion_TruncatesOnlyBeyondTwoHundredCharacters(int length)
    {
        var criterion = string.Concat(Enumerable.Range(0, length).Select(i => (char)('a' + i % 26)));
        var output = await WaiveSecondCriterion(criterion);
        var text = PrintedText(ConfirmationLine(output));

        Xunit.Assert.Equal(length > 200 ? criterion[..200] + "..." : criterion, text);
        Xunit.Assert.InRange(text.Length, 1, 203);
        Xunit.Assert.DoesNotContain("\r", text);
        Xunit.Assert.DoesNotContain("\n", text);
    }

    [Xunit.Theory]
    [Xunit.InlineData("first half\r\nsecond half")]
    [Xunit.InlineData("first half\rsecond half")]
    [Xunit.InlineData("first half\nsecond half")]
    [Xunit.InlineData(" \tfirst   half\r\n\t second\u2003half  ")]
    public async Task WaiveWhitespaceCriterion_CollapsesTextToOneOutputLine(string criterion)
    {
        var output = await WaiveSecondCriterion(criterion);
        var confirmation = ConfirmationLine(output);

        Xunit.Assert.Equal("first half second half", PrintedText(confirmation));
        Xunit.Assert.Single(OutputLines(output), line => line.Contains("text=", StringComparison.Ordinal));
        Xunit.Assert.DoesNotContain("\r", confirmation);
        Xunit.Assert.DoesNotContain("\n", confirmation);
    }

    [Xunit.Fact]
    public async Task WaiveWhitespaceHeavyCriterion_CollapsesBeforeMeasuringLimit()
    {
        var firstHalf = new string('a', 100);
        var secondHalf = new string('b', 99);
        var output = await WaiveSecondCriterion(firstHalf + "\r\n\t   " + secondHalf);

        Xunit.Assert.Equal(firstHalf + " " + secondHalf, PrintedText(ConfirmationLine(output)));
    }

    [Xunit.Fact]
    public async Task WaiveSuccessfulCriterion_PrintsIndexConventionNoteAfterConfirmation()
    {
        var output = await WaiveSecondCriterion("beta criterion two");
        var lines = OutputLines(output);
        var confirmationIndex = Array.IndexOf(lines, ConfirmationLine(output));

        Xunit.Assert.Equal(
            "note: goal-amend --waive criterion numbers are one-based; Reviewer criterion_index values are zero-based.",
            lines[confirmationIndex + 1]);
    }

    private static async Task<string> WaiveSecondCriterion(string criterion)
    {
        var workspace = CreateRefinedWorkspace(CreateTempDirectory());
        var repository = CreateMigratedStateRepository(workspace.SqliteStatePath);
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Expose waived criterion");
        kernel.SetGoalRefinedSpec(goal.Id, new RefinedSpec(
            "Expose the resolved criterion in CLI output.",
            ["alpha criterion one", criterion, "gamma criterion three"],
            VerificationClass.TestVerifiable,
            [],
            []));
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        await repository.SaveAsync(kernel);
        var restored = await repository.LoadAsync();
        Xunit.Assert.Equal(criterion, restored.GetGoal(goal.Id).RefinedSpec!.AcceptanceCriteria[1]);
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;

        return CaptureConsole(() =>
        {
            var changed = CliPersistentStateRunner.ExecuteCommand(
                CliArgumentParser.SplitCommand(
                    $"goal-amend {goal.Id.Value[..8]} --waive 2 --reason \"scope clarified\""),
                repository,
                workspace,
                ref agents,
                providers,
                ref profiles,
                ref currentGoal);
            Xunit.Assert.True(changed);
        });
    }

    private static string[] OutputLines(string output) =>
        output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);

    private static string ConfirmationLine(string output) =>
        Xunit.Assert.Single(OutputLines(output), line =>
            line.StartsWith("Acceptance criterion waived:", StringComparison.Ordinal));

    private static string PrintedText(string confirmation)
    {
        const string textField = " text=";
        var start = confirmation.IndexOf(textField, StringComparison.Ordinal);
        Xunit.Assert.True(start >= 0, "Waiver confirmation must include the text= field.");
        start += textField.Length;
        var end = confirmation.IndexOf(" actor=", start, StringComparison.Ordinal);
        Xunit.Assert.True(end >= start, "The actor= field must follow the waived text.");
        return confirmation[start..end];
    }
}
