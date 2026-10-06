using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Probe = SourceRevertedDeclaredProbeFixture;

[Xunit.Collection(TestCollections.JobAccounting)]
public sealed class GoalAcceptanceVerifierDotnetBuildSlotTestsSourceRevertedDeclaredSet : GoalAcceptanceVerifierDotnetBuildSlotTests
{
    [Fact]
    public async Task DeclaredSupport_RestoresModifiedAndDropsAdded_PreservesCandidateTests()
    {
        using var fixture = new Probe();
        var calls = 0;
        string? variant = null;
        var verifier = CreateVerifier(fixture, "SupportProbeTests.OneLaunch", worktree =>
        {
            calls++;
            variant = worktree;
            Assert.Equal(Probe.MainSupport, Read(worktree, Probe.Support));
            Assert.False(File.Exists(Path.Combine(worktree, Probe.Counter)));
            foreach (var path in new[] { Probe.Selected, Probe.Legacy, Probe.Policy })
                Assert.Equal(File.ReadAllBytes(Path.Combine(fixture.Root, path)), File.ReadAllBytes(Path.Combine(worktree, path)));
        });
        var result = await fixture.RunAsync(verifier, "SupportProbeTests", [Probe.Support, Probe.Counter], Probe.Declared);
        Assert.Equal(FindingEvidenceNegativeControlOutcome.Demonstrated, result.NegativeControlOutcome);
        Assert.Equal(1, calls);
        var arm = Assert.Single(result.Arms!, arm => arm.Arm == FindingEvidenceArm.SourceReverted);
        Assert.Equal([Probe.Support], arm.RestoredPaths);
        Assert.Equal([Probe.Counter], arm.DroppedPaths);
        Assert.NotNull(variant);
        Assert.False(Directory.Exists(variant));
        Assert.DoesNotContain(variant.Replace('\\', '/'), fixture.Git("worktree", "list", "--porcelain").Replace('\\', '/'), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(fixture.CandidateSha, fixture.Git("rev-parse", "HEAD").Trim());
        Assert.Equal(Probe.CandidateSupport, fixture.Read(Probe.Support));
        Assert.Empty(fixture.Git("status", "--porcelain").Trim());
    }

    [Fact]
    public async Task DeclaredConfigAndSkill_RestoreOnlyNamedPaths()
    {
        using var fixture = new Probe();
        var calls = 0;
        var verifier = CreateVerifier(fixture, "SupportProbeTests.OneLaunch", worktree =>
        {
            calls++;
            Assert.Equal("""{ "launches": 2 }""", Read(worktree, Probe.Policy));
            Assert.Equal("Probe guide v1", Read(worktree, Probe.Guide));
            Assert.Equal(Probe.CandidateSupport, Read(worktree, Probe.Support));
        });
        var result = await fixture.RunAsync(verifier, "SupportProbeTests", [Probe.Policy, Probe.Guide], Probe.Declared);
        Assert.Equal(FindingEvidenceNegativeControlOutcome.Demonstrated, result.NegativeControlOutcome);
        Assert.Equal(1, calls);
        var arm = Assert.Single(result.Arms!, arm => arm.Arm == FindingEvidenceArm.SourceReverted);
        Assert.Equal([Probe.Guide, Probe.Policy], arm.RestoredPaths);
        Assert.Empty(arm.DroppedPaths!);
    }

    [Theory]
    [InlineData(Probe.Policy, false, FindingEvidenceRevertPathsRejection.OutsideSrc)]
    [InlineData(Probe.Guide, false, FindingEvidenceRevertPathsRejection.OutsideSrc)]
    [InlineData(Probe.Support, true, FindingEvidenceRevertPathsRejection.UnderTests)]
    [InlineData(Probe.Unchanged, true, FindingEvidenceRevertPathsRejection.NotChangedByGoal)]
    public async Task UndeclaredOrUnchangedPath_RejectsWithoutVariant(string path, bool declare,
        FindingEvidenceRevertPathsRejection expected)
    {
        using var fixture = new Probe();
        var calls = 0;
        var verifier = CreateVerifier(fixture, "SupportProbeTests.OneLaunch", _ => { }, () => calls++);
        var declared = declare ? Probe.Declared.Where(item => item != Probe.Support).ToArray() : null;
        var result = await fixture.RunAsync(verifier, "SupportProbeTests", [path], declared);
        AssertRejection(result, expected);
        Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData("SupportProbeTests", Probe.Selected, false, FindingEvidenceRevertPathsRejection.SelectedTestClass)]
    [InlineData("LegacyProbeTests", Probe.Legacy, false, FindingEvidenceRevertPathsRejection.SelectedTestClass)]
    [InlineData("SupportProbeTests", Probe.Selected, true, FindingEvidenceRevertPathsRejection.MutationSelectedTestClass)]
    public async Task SelectedTestDeclaration_RejectsBeforeWorktree(string selection, string path, bool mutate,
        FindingEvidenceRevertPathsRejection expected)
    {
        using var fixture = new Probe();
        var calls = 0;
        var verifier = CreateVerifier(fixture, selection + (selection == "LegacyProbeTests" ? ".Legacy" : ".OneLaunch"), _ => { }, () => calls++);
        var result = await fixture.RunAsync(verifier, selection, mutate ? null : [path], Probe.Declared,
            mutate ? new FindingEvidenceMutation(path, "Equal(1", "Equal(2") : null);
        AssertRejection(result, expected);
        Assert.Equal(0, calls);
        Assert.Single(fixture.Git("worktree", "list", "--porcelain").Split('\n').Where(line => line.StartsWith("worktree ", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task SelectedClassAtMergeBase_RejectsEvenWhenCandidateRenamesIt()
    {
        using var fixture = new Probe();
        fixture.Write(Probe.Legacy, "public sealed class RenamedProbeTests { }");
        fixture.Git("add", ".");
        fixture.Git("commit", "-m", "rename legacy declaration");
        var calls = 0;
        var verifier = CreateVerifier(fixture, "LegacyProbeTests.Legacy", _ => { }, () => calls++);
        var result = await fixture.RunAsync(verifier, "LegacyProbeTests", [Probe.Legacy], Probe.Declared);
        AssertRejection(result, FindingEvidenceRevertPathsRejection.SelectedTestClass);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task DeclaredSupportMutation_LeavesOtherCandidateBytesIntact()
    {
        using var fixture = new Probe();
        var calls = 0;
        var verifier = CreateVerifier(fixture, "SupportProbeTests.OneLaunch", worktree =>
        {
            calls++;
            Assert.Equal(Probe.MainSupport, Read(worktree, Probe.Support));
            Assert.Equal(fixture.Read(Probe.Selected), Read(worktree, Probe.Selected));
        });
        var result = await fixture.RunAsync(verifier, "SupportProbeTests", null, Probe.Declared,
            new FindingEvidenceMutation(Probe.Support, "Launches => 1", "Launches => 2"));
        Assert.Equal(FindingEvidenceNegativeControlOutcome.Demonstrated, result.NegativeControlOutcome);
        Assert.Equal(1, calls);
        var arm = Assert.Single(result.Arms!, arm => arm.Arm == FindingEvidenceArm.SourceReverted);
        Assert.Empty(arm.RestoredPaths!);
        Assert.Empty(arm.DroppedPaths!);
    }

    [Fact]
    public async Task DeclarationWithoutExplicitScope_KeepsWholeSourceRevert()
    {
        using var fixture = new SourceRevertedProbeFixture();
        TestOverrides.BaselineIntegrityLabelerForTests = new SuccessfulLabeler();
        var calls = 0;
        var verifier = new GoalAcceptanceVerifier(TestOverrides, (args, worktree, _) =>
        {
            var reverted = !string.Equals(worktree, fixture.Root, StringComparison.OrdinalIgnoreCase);
            if (reverted)
            {
                Assert.Equal(SourceRevertedProbeFixture.BaselineFeature, Read(worktree, SourceRevertedProbeFixture.Feature));
                Assert.Equal(fixture.CandidateTest, Read(worktree, SourceRevertedProbeFixture.Test));
            }
            if (Array.IndexOf(args, "--results-directory") >= 0)
            {
                if (reverted) calls++;
                WriteMtpTrx(args, 1, [SourceRevertedProbeFixture.Identity]);
                if (reverted) SourceRevertedProbeFixture.MakeTrxRed(args);
                return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(reverted ? 1 : 0, reverted ? "Failed: 1" : "Passed: 1"));
            }
            return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "Build succeeded."));
        });
        var result = await verifier.RunFocusedEvidenceAsync(fixture.Root, GoalId.New(), "Core.Tests: AddedProbeTests",
            negativeControl: FindingEvidenceNegativeControl.RevertSrc, declaredPaths: [SourceRevertedProbeFixture.Test]);
        Assert.Equal(FindingEvidenceNegativeControlOutcome.Demonstrated, result.NegativeControlOutcome);
        Assert.Equal(1, calls);
        var arm = Assert.Single(result.Arms!, arm => arm.Arm == FindingEvidenceArm.SourceReverted);
        Assert.All(arm.RestoredPaths!, path => Assert.StartsWith("src/", path, StringComparison.Ordinal));
        Assert.All(arm.DroppedPaths!, path => Assert.StartsWith("src/", path, StringComparison.Ordinal));
    }

    private GoalAcceptanceVerifier CreateVerifier(Probe fixture, string identity, Action<string> observe, Action? variantInvocation = null)
    {
        TestOverrides.BaselineIntegrityLabelerForTests = new SuccessfulLabeler();
        return new GoalAcceptanceVerifier(TestOverrides, (args, worktree, _) =>
        {
            var reverted = !string.Equals(worktree, fixture.Root, StringComparison.OrdinalIgnoreCase);
            if (reverted) variantInvocation?.Invoke();
            if (Array.IndexOf(args, "--results-directory") >= 0)
            {
                if (reverted) observe(worktree);
                WriteMtpTrx(args, 1, [identity]);
                if (reverted) SourceRevertedProbeFixture.MakeTrxRed(args);
                return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(reverted ? 1 : 0, reverted ? "Failed: 1" : "Passed: 1"));
            }
            return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "Build succeeded."));
        });
    }

    private static void AssertRejection(FocusedEvidenceRunResult result, FindingEvidenceRevertPathsRejection expected)
    {
        Assert.Equal(expected, result.RevertPathsRejection);
        Assert.Equal(FindingEvidenceNegativeControlOutcome.Inconclusive, result.NegativeControlOutcome);
        Assert.Contains(FindingEvidenceRevertPathsRejectionJsonConverter.ToWireValue(expected), result.Summary, StringComparison.Ordinal);
        var arm = Assert.Single(result.Arms!, arm => arm.Arm == FindingEvidenceArm.SourceReverted);
        Assert.Equal(FindingEvidenceArmDisposition.Inconclusive, arm.Disposition);
        Assert.Equal(expected, arm.RevertPathsRejection);
        Assert.Empty(arm.Checks);
    }

    private static string Read(string root, string path) => File.ReadAllText(Path.Combine(root, path));
    private sealed class SuccessfulLabeler : IWorkerIntegrityLabeler
    {
        public IntegrityLabelState Query(string path) => new(true, true, true);
        public bool SetIntegrity(string path, string level, bool recursive) => true;
    }
}
