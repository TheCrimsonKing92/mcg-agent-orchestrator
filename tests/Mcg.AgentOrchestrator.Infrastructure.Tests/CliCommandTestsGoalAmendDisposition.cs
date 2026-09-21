using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.SubscriptionPlanning;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection(TestCollections.CliProcessEnvironment)]
public sealed class CliCommandTestsGoalAmendDisposition : CliCommandTestBase
{
    [Xunit.Fact]
    public async Task GoalAmendReadsDispositionFilePersistsItAndRendersItAgainstAffectedCriterion()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var repository = CreateMigratedStateRepository(workspace.SqliteStatePath);
        var kernel = new AgentOrchestratorKernel();
        var goal = CreateActiveGoal(kernel);
        kernel.MapCriterionEvidenceOwner(
            goal.Id,
            criterionIndex: 1,
            criterionVersion: 1,
            CriterionEvidenceOwner.Acceptance,
            "operator",
            CriterionEvidenceScopes.FullAcceptanceGate,
            expectedCandidateSha: "candidate-a");
        await repository.SaveAsync(kernel);
        var reasonPath = Path.Combine(root, "waiver-reason.txt");
        var dispositionPath = Path.Combine(root, "criterion-disposition.txt");
        await File.WriteAllTextAsync(reasonPath, "mechanism moved to follow-up work");
        await File.WriteAllTextAsync(dispositionPath, "threshold is no longer applicable:\r\nmeasure the replacement instead");
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;

        var changed = CliPersistentStateRunner.ExecuteCommand(
            CliArgumentParser.SplitCommand(
                $"goal-amend {goal.Id.Value[..8]} --waive 1 --reason-file {reasonPath} --disposition-file 2={dispositionPath} --actor miles"),
            repository,
            workspace,
            ref agents,
            providers,
            ref profiles,
            ref currentGoal);

        var restored = await repository.LoadAsync();
        var restoredGoal = restored.GetGoal(goal.Id);
        var waiver = Xunit.Assert.Single(restoredGoal.EffectiveAcceptanceCriteriaCorrections);
        var disposition = Xunit.Assert.Single(waiver.Dispositions!);
        var output = CaptureConsole(() => ConsoleViews.PrintGoal(restoredGoal));
        var criterionOffset = output.IndexOf("  2. Measure after landing.", StringComparison.Ordinal);
        var dispositionOffset = output.IndexOf("disposition (waiver of Implement the mechanism.)", StringComparison.Ordinal);
        var nextCriterionOffset = output.IndexOf("  3. Preserve unrelated behavior.", StringComparison.Ordinal);

        Xunit.Assert.True(changed);
        Xunit.Assert.Equal("Measure after landing.", disposition.Criterion);
        Xunit.Assert.Equal("threshold is no longer applicable: measure the replacement instead", disposition.Disposition);
        Xunit.Assert.True(criterionOffset >= 0 && dispositionOffset > criterionOffset && nextCriterionOffset > dispositionOffset);
        Xunit.Assert.Contains("[miles ", output, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void GoalAmendRequiresDispositionAcceptsInlineFormAndRendersLegacyAbsence()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var kernel = new AgentOrchestratorKernel();
        var affectedGoal = CreateActiveGoal(kernel);
        kernel.MapCriterionEvidenceOwner(
            affectedGoal.Id,
            criterionIndex: 1,
            criterionVersion: 1,
            CriterionEvidenceOwner.Acceptance,
            "operator",
            CriterionEvidenceScopes.FullAcceptanceGate,
            expectedCandidateSha: "candidate-a");

        var error = Xunit.Assert.Throws<InvalidOperationException>(() => ExecuteCliAndCapture(
            ["goal-amend", affectedGoal.Id.Value[..8], "--waive", "1", "--reason", "moved"],
            kernel,
            workspace));

        var legacyGoal = kernel.CreateGoal("Render legacy waiver absence");
        kernel.SetGoalRefinedSpec(legacyGoal.Id, new RefinedSpec(
            "Render absence honestly.",
            ["Legacy criterion."],
            VerificationClass.TestVerifiable,
            [],
            []));
        kernel.ActivateGoal(legacyGoal.Id, AgentCatalog.Default().Agents);
        kernel.WaiveAcceptanceCriterion(legacyGoal.Id, "1", "old waiver");
        var output = CaptureConsole(() => ConsoleViews.PrintGoal(legacyGoal));

        Xunit.Assert.Contains("Measure after landing.", error.Message, StringComparison.Ordinal);
        Xunit.Assert.Empty(affectedGoal.EffectiveAcceptanceCriteriaCorrections);

        ExecuteCliAndCapture(
            [
                "goal-amend", affectedGoal.Id.Value[..8], "--waive", "1", "--reason", "moved",
                "--disposition", "2=criterion remains meaningful"
            ],
            kernel,
            workspace);
        var inlineDisposition = Xunit.Assert.Single(
            Xunit.Assert.Single(affectedGoal.EffectiveAcceptanceCriteriaCorrections).Dispositions!);
        Xunit.Assert.Equal("criterion remains meaningful", inlineDisposition.Disposition);

        Xunit.Assert.Contains("disposition: no disposition recorded", output, StringComparison.Ordinal);
    }

    private static Goal CreateActiveGoal(AgentOrchestratorKernel kernel)
    {
        var goal = kernel.CreateGoal("Protect deferred premise");
        kernel.SetGoalRefinedSpec(goal.Id, new RefinedSpec(
            "Require explicit dispositions.",
            ["Implement the mechanism.", "Measure after landing.", "Preserve unrelated behavior."],
            VerificationClass.TestVerifiable,
            [],
            []));
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        return goal;
    }
}
