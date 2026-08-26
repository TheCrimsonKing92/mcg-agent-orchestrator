using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class PlannerCandidateSelectorTests
{
    [Xunit.Fact]
    public void TwoValidCandidates_StructuralEvidence_SelectsStrongerPlan()
    {
        var repositoryRoot = InfrastructureTestSupport.FindRepositoryRoot();
        var primary = ReadPlannerFixture(repositoryRoot).Replace(
            "## Risks and stop conditions",
            string.Concat(Enumerable.Repeat(
                "Extra explanatory prose does not constitute structural evidence. ",
                20)) +
            "\n\n## Risks and stop conditions",
            StringComparison.Ordinal);
        var stronger = ReadPlannerFixture(repositoryRoot);
        string[] mappingHeadings =
        [
            "1. Exit-0 Tester blockers must not complete or verify the task.",
            "2. Preserve discriminating evidence and correct routing.",
            "3. Preserve unaffected contracts.",
            "4. Cover reconciliation and acceptance state.",
            "5. Rule (l)."
        ];
        for (var index = 0; index < mappingHeadings.Length; index++)
        {
            stronger = stronger.Replace(
                mappingHeadings[index],
                $"{index + 1}. disposition=planned; plan=Developer owns `PlannerCandidateSelector.Select`; " +
                "the integration seam is TEST-VERIFIABLE and stops when candidate evidence is unavailable.",
                StringComparison.Ordinal);
        }

        var result = PlannerCandidateSelector.Select(
            [new(0, primary), new(1, stronger)],
            repositoryRoot);

        Xunit.Assert.True(result.SelectedContract.Succeeded, result.SelectedContract.Diagnostic);
        Xunit.Assert.Equal(1, result.Receipt.SelectedCandidateIndex);
        Xunit.Assert.Equal("structural-quality", result.Receipt.SelectionReason);
        var evidence = Xunit.Assert.Single(result.Receipt.Candidates!, candidate => candidate.CandidateIndex == 1);
        Xunit.Assert.Equal(PlannerCandidateContractVerdict.Valid, evidence.ContractVerdict);
        Xunit.Assert.Equal(mappingHeadings.Length, evidence.StructuralQuality!.ConcreteOwningSeams);
    }

    [Xunit.Fact]
    public void IdenticalCandidates_SelectPrimaryWithExplicitReason()
    {
        var repositoryRoot = InfrastructureTestSupport.FindRepositoryRoot();
        var plan = ReadPlannerFixture(repositoryRoot);

        var result = PlannerCandidateSelector.Select([new(0, plan), new(1, plan)], repositoryRoot);

        Xunit.Assert.Equal(0, result.Receipt.SelectedCandidateIndex);
        Xunit.Assert.Equal("identical-candidates", result.Receipt.SelectionReason);
        Xunit.Assert.Equal("normalized-candidate-hashes-identical", result.Receipt.FallbackCause);
    }

    [Xunit.Fact]
    public void InvalidSecondary_SelectPrimaryWithTypedFallback()
    {
        var repositoryRoot = InfrastructureTestSupport.FindRepositoryRoot();
        var plan = ReadPlannerFixture(repositoryRoot);

        var result = PlannerCandidateSelector.Select([new(0, plan), new(1, "")], repositoryRoot);

        Xunit.Assert.True(result.SelectedContract.Succeeded, result.SelectedContract.Diagnostic);
        Xunit.Assert.Equal(0, result.Receipt.SelectedCandidateIndex);
        Xunit.Assert.Equal("primary-fallback", result.Receipt.SelectionReason);
        Xunit.Assert.Equal("fewer-than-two-comparable-candidates", result.Receipt.FallbackCause);
    }

    [Xunit.Fact]
    public void InvalidPrimary_ValidSecondary_FailsClosedWithoutSelection()
    {
        var repositoryRoot = InfrastructureTestSupport.FindRepositoryRoot();
        var plan = ReadPlannerFixture(repositoryRoot);

        var result = PlannerCandidateSelector.Select([new(0, "invalid"), new(1, plan)], repositoryRoot);

        Xunit.Assert.False(result.SelectedContract.Succeeded);
        Xunit.Assert.Null(result.Receipt.SelectedCandidateIndex);
        Xunit.Assert.Equal("no-selection", result.Receipt.SelectionReason);
    }

    [Xunit.Fact]
    public void AllInvalidCandidates_FailClosedWithoutSelection()
    {
        var repositoryRoot = InfrastructureTestSupport.FindRepositoryRoot();

        var result = PlannerCandidateSelector.Select([new(0, ""), new(1, "invalid")], repositoryRoot);

        Xunit.Assert.False(result.SelectedContract.Succeeded);
        Xunit.Assert.Null(result.Receipt.SelectedCandidateIndex);
        Xunit.Assert.All(result.Receipt.Candidates!, candidate =>
            Xunit.Assert.NotEqual(PlannerCandidateContractVerdict.Valid, candidate.ContractVerdict));
    }

    [Xunit.Fact]
    public void PeerAgreementSelectsConsensusInsteadOfFirstShortestValidPlan()
    {
        var repositoryRoot = InfrastructureTestSupport.FindRepositoryRoot();
        var shortestOutlier = ReadPlannerFixture(repositoryRoot);
        var consensus = shortestOutlier.Replace(
            "## Risks and stop conditions",
            "The dispatch selection seam records candidate agreement for downstream role handoff and deterministic review evidence.\n\n## Risks and stop conditions",
            StringComparison.Ordinal);
        var consensusPeer = consensus.Replace(
            "deterministic review evidence.",
            "deterministic review evidence with independently phrased bounded details.",
            StringComparison.Ordinal);
        var selectedSourcePath = Path.Combine(repositoryRoot, "planner.sample-1.out.log");

        var result = PlannerCandidateSelector.Select(
            [
                new(0, shortestOutlier),
                new(1, consensus, SourcePath: selectedSourcePath),
                new(2, consensusPeer, SourcePath: Path.Combine(repositoryRoot, "planner.sample-2.out.log"))
            ],
            repositoryRoot);
        var expectedConsensus = PlannerOutputContract.Resolve(consensus, string.Empty, repositoryRoot).Plan!;

        Xunit.Assert.True(shortestOutlier.Length < consensus.Length);
        Xunit.Assert.NotEqual(consensus, consensusPeer);
        Xunit.Assert.Equal(1, result.Receipt.SelectedCandidateIndex);
        Xunit.Assert.Equal(
            expectedConsensus.ReplaceLineEndings("\n"),
            result.SelectedContract.Plan!.ReplaceLineEndings("\n"));
        Xunit.Assert.Equal(selectedSourcePath, result.SelectedContract.IngestedPath);
        Xunit.Assert.Equal(PlannerCandidateSelector.SelectionSignal, result.Receipt.SelectionSignal);
        Xunit.Assert.True(result.Receipt.CandidateScores[1] > result.Receipt.CandidateScores[0]);
        Xunit.Assert.Contains(result.Receipt.Sections, section =>
            section.Section.Equals("verification commands and classes", StringComparison.OrdinalIgnoreCase));
    }

    [Xunit.Fact]
    public void ThreeConfiguredCandidates_TwoValid_SelectsByStructuralQuality()
    {
        var repositoryRoot = InfrastructureTestSupport.FindRepositoryRoot();
        var primary = ReadPlannerFixture(repositoryRoot);
        var structurallyStronger = primary;
        string[] mappingHeadings =
        [
            "1. Exit-0 Tester blockers must not complete or verify the task.",
            "2. Preserve discriminating evidence and correct routing.",
            "3. Preserve unaffected contracts.",
            "4. Cover reconciliation and acceptance state.",
            "5. Rule (l)."
        ];
        for (var index = 0; index < mappingHeadings.Length; index++)
        {
            structurallyStronger = structurallyStronger.Replace(
                mappingHeadings[index],
                $"{index + 1}. disposition=planned; plan=Developer owns `PlannerCandidateSelector.Select`; " +
                "the integration seam is TEST-VERIFIABLE and stops when candidate evidence is unavailable.",
                StringComparison.Ordinal);
        }

        var result = PlannerCandidateSelector.Select(
            [new(0, primary), new(1, structurallyStronger), new(2, "invalid")],
            repositoryRoot);

        Xunit.Assert.Equal(1, result.Receipt.SelectedCandidateIndex);
        Xunit.Assert.Equal("structural-quality", result.Receipt.SelectionReason);
    }

    [Xunit.Fact]
    public void StructuralQuality_DuplicateAndOutOfRangeMappingsDoNotInflateVector()
    {
        var repositoryRoot = InfrastructureTestSupport.FindRepositoryRoot();
        var plan = ReplaceSectionBody(
            ReadPlannerFixture(repositoryRoot),
            "## Acceptance criteria mapping",
            "1. disposition=planned; plan=Acceptance owns `PlannerCandidateSelectorTests.cs`; the integration seam is TEST-VERIFIABLE and stops on failure.\n" +
            "1. disposition=planned; plan=Acceptance owns `Duplicate.cs`; the integration seam is TEST-VERIFIABLE and stops on failure.\n" +
            "99. disposition=planned; plan=Acceptance owns `OutOfRange.cs`; the integration seam is TEST-VERIFIABLE and stops on failure.");

        var quality = PlannerOutputContract.EvaluateStructuralQuality(plan);

        Xunit.Assert.Equal(1, quality.CompleteMappings);
        Xunit.Assert.Equal(1, quality.ConcreteOwningSeams);
        Xunit.Assert.Equal(1, quality.FeasibleEvidenceOwners);
    }

    [Xunit.Fact]
    public void StructuralQuality_EvidenceOwnerMustBeFeasibleForVerificationClass()
    {
        var repositoryRoot = InfrastructureTestSupport.FindRepositoryRoot();
        var fixture = ReadPlannerFixture(repositoryRoot);
        var testPlan = ReplaceSectionBody(
            fixture,
            "## Acceptance criteria mapping",
            "1. disposition=planned; plan=Researcher owns `PlannerCandidateSelectorTests`; Developer is informed, but the integration seam is TEST-VERIFIABLE and stops on failure.");
        var realWorldPlan = ReplaceSectionBody(
            fixture,
            "## Acceptance criteria mapping",
            "1. disposition=planned; plan=operator owns `paired-run-receipt`; the integration seam is REAL-WORLD-DEPENDENT and stops when evidence is unavailable.");

        var testQuality = PlannerOutputContract.EvaluateStructuralQuality(testPlan);
        var realWorldQuality = PlannerOutputContract.EvaluateStructuralQuality(realWorldPlan);

        Xunit.Assert.Equal(0, testQuality.FeasibleEvidenceOwners);
        Xunit.Assert.Equal(1, realWorldQuality.FeasibleEvidenceOwners);
    }

    [Xunit.Fact]
    public void StructuralQuality_TestOwnerCapabilityMustMatchRequestedEvidence()
    {
        var repositoryRoot = InfrastructureTestSupport.FindRepositoryRoot();
        var fixture = ReadPlannerFixture(repositoryRoot);
        var testerFullSuitePlan = ReplaceSectionBody(
            fixture,
            "## Acceptance criteria mapping",
            "1. disposition=planned; plan=Tester owns full-suite test execution for `PlannerCandidateSelectorTests`; the integration seam is TEST-VERIFIABLE and stops on failure.");
        var acceptanceFullSuitePlan = ReplaceSectionBody(
            fixture,
            "## Acceptance criteria mapping",
            "1. disposition=planned; plan=Acceptance owns full-suite test execution for `PlannerCandidateSelectorTests`; the integration seam is TEST-VERIFIABLE and stops on failure.");
        var testerBuildPlan = ReplaceSectionBody(
            fixture,
            "## Acceptance criteria mapping",
            "1. disposition=planned; plan=Tester owns the worker build result and manual reproduction for `PlannerCandidateSelectorTests`; the integration seam is TEST-VERIFIABLE and stops on failure.");
        var reviewerFocusedEvidencePlan = ReplaceSectionBody(
            fixture,
            "## Acceptance criteria mapping",
            "1. disposition=planned; plan=Reviewer owns the focused-evidence request for `PlannerCandidateSelectorTests`; the integration seam is TEST-VERIFIABLE and stops on failure.");
        var researcherFocusedEvidencePlan = ReplaceSectionBody(
            fixture,
            "## Acceptance criteria mapping",
            "1. disposition=planned; plan=Researcher owns the focused-evidence request for `PlannerCandidateSelectorTests`; the integration seam is TEST-VERIFIABLE and stops on failure.");
        var researcherSourceTracePlan = ReplaceSectionBody(
            fixture,
            "## Acceptance criteria mapping",
            "1. disposition=planned; plan=Researcher owns the source trace for `PlannerCandidateSelectorTests`; the integration seam is TEST-VERIFIABLE and stops on failure.");

        var testerFullSuite = PlannerOutputContract.EvaluateStructuralQuality(testerFullSuitePlan);
        var acceptanceFullSuite = PlannerOutputContract.EvaluateStructuralQuality(acceptanceFullSuitePlan);
        var testerBuild = PlannerOutputContract.EvaluateStructuralQuality(testerBuildPlan);
        var reviewerFocusedEvidence = PlannerOutputContract.EvaluateStructuralQuality(reviewerFocusedEvidencePlan);
        var researcherFocusedEvidence = PlannerOutputContract.EvaluateStructuralQuality(researcherFocusedEvidencePlan);
        var researcherSourceTrace = PlannerOutputContract.EvaluateStructuralQuality(researcherSourceTracePlan);

        Xunit.Assert.Equal(0, testerFullSuite.FeasibleEvidenceOwners);
        Xunit.Assert.Equal(1, acceptanceFullSuite.FeasibleEvidenceOwners);
        Xunit.Assert.Equal(1, testerBuild.FeasibleEvidenceOwners);
        Xunit.Assert.Equal(1, reviewerFocusedEvidence.FeasibleEvidenceOwners);
        Xunit.Assert.Equal(0, researcherFocusedEvidence.FeasibleEvidenceOwners);
        Xunit.Assert.Equal(1, researcherSourceTrace.FeasibleEvidenceOwners);
    }

    [Xunit.Fact]
    public void SparseCandidateIndexesFailWithTypedArgumentError()
    {
        var repositoryRoot = InfrastructureTestSupport.FindRepositoryRoot();
        var plan = ReadPlannerFixture(repositoryRoot);

        var exception = Xunit.Assert.Throws<ArgumentException>(() =>
            PlannerCandidateSelector.Select([new(0, plan), new(2, plan)], repositoryRoot));

        Xunit.Assert.Equal("candidates", exception.ParamName);
        Xunit.Assert.Contains("contiguous", exception.Message, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void DivergenceHashesIgnoreLineEndingDifferences()
    {
        var repositoryRoot = InfrastructureTestSupport.FindRepositoryRoot();
        var plan = ReadPlannerFixture(repositoryRoot).ReplaceLineEndings("\n");
        var divergent = plan.Replace(
            "## Risks and stop conditions",
            "A distinct dispatch seam changes the recorded evidence.\n\n## Risks and stop conditions",
            StringComparison.Ordinal);

        var result = PlannerCandidateSelector.Select(
            [new(0, divergent), new(1, plan), new(2, plan.ReplaceLineEndings("\r\n"))],
            repositoryRoot);

        var section = Xunit.Assert.Single(result.Receipt.Sections, candidate =>
            candidate.Section.Equals("verification commands and classes", StringComparison.OrdinalIgnoreCase));
        var firstConsensus = Xunit.Assert.Single(section.Candidates, candidate => candidate.CandidateIndex == 1);
        var secondConsensus = Xunit.Assert.Single(section.Candidates, candidate => candidate.CandidateIndex == 2);
        Xunit.Assert.Equal(firstConsensus.ContentHash, secondConsensus.ContentHash);
    }

    private static string ReadPlannerFixture(string repositoryRoot) => File.ReadAllText(Path.Combine(
            repositoryRoot,
            "tests", "Mcg.AgentOrchestrator.Infrastructure.Tests", "Fixtures", "PlannerOutputContract",
            "658501ce-f6708f44-20260805012800.out.txt"))
        .ReplaceLineEndings("\n");

    private static string ReplaceSectionBody(string plan, string heading, string body)
    {
        var headingStart = plan.IndexOf(heading, StringComparison.Ordinal);
        Xunit.Assert.True(headingStart >= 0, $"Missing fixture heading '{heading}'.");
        var bodyStart = plan.IndexOf('\n', headingStart) + 1;
        var nextHeading = plan.IndexOf("\n## ", bodyStart, StringComparison.Ordinal);
        Xunit.Assert.True(bodyStart > 0 && nextHeading > bodyStart);
        return plan[..bodyStart] + body + plan[nextHeading..];
    }
}
