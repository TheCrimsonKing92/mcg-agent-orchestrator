using System.Text.Json;
using Mcg.AgentOrchestrator.Core;

// Parallel-safe: all state and clocks belong to the individual case.
public sealed class PostLandingObligationCoreTests
{
    private static readonly string Candidate = new('a', 40);
    private static readonly CriterionObservationScope Scope = new(CriterionObservationKind.Runtime,
        "An organic operation uses the landed behavior", "local-runtime");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Refinement_ExplicitLaterDuty_PersistsWithoutLandingPrerequisite(bool reload)
    {
        var (kernel, goal, _) = Create();
        if (reload) { kernel = Reload(kernel); goal = kernel.GetGoal(goal.Id); }
        var duty = Assert.Single(goal.OutstandingPostLandingDuties);
        Assert.Equal(CriterionEvidencePhase.PostLanding, duty.Phase);
        Assert.Equal(CriterionEvidenceState.Pending, duty.State);
        Assert.Equal(Scope, duty.ObservationScope);
        Assert.Empty(goal.LandingPrerequisiteObligations);
        Assert.Empty(goal.GetLandingPrerequisiteObligations(Candidate));
        Assert.Single(goal.OutstandingCriterionEvidenceObligations);
    }

    [Theory]
    [InlineData("unknown-phase")]
    [InlineData("unsupported-owner")]
    [InlineData("missing-scope")]
    [InlineData("missing-target")]
    [InlineData("mixed")]
    public void Refinement_InvalidDeclaration_RejectsBeforeMutation(string scenario)
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Validate lifecycle declaration");
        var spec = Spec();
        var declaration = Assert.Single(spec.CriterionEvidenceDeclarations);
        spec = scenario switch
        {
            "unknown-phase" => spec with { CriterionEvidenceDeclarations = [declaration with { Phase = (CriterionEvidencePhase)42 }] },
            "unsupported-owner" => spec with { OperatorOwnedAcceptanceCriteria = [] },
            "missing-scope" => spec with { CriterionEvidenceDeclarations = [declaration with { ObservationScope = null }] },
            "missing-target" => spec with { CriterionEvidenceDeclarations = [declaration with { ObservationScope = Scope with { Target = "" } }] },
            _ => spec with { CriterionEvidenceDeclarations = [declaration with { Atomic = false }] }
        };
        Assert.Throws<ArgumentException>(() => kernel.RecordGoalRefinement(goal.Id, spec));
        Assert.Empty(goal.RefinedSpecVersions);
        Assert.Empty(goal.CriterionEvidenceObligations);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Restore_LegacyOrInvalidPhase_RemainsBlocking(bool invalid)
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Legacy operator control");
        kernel.RecordGoalRefinement(goal.Id, Spec() with { CriterionEvidenceDeclarations = [] });
        var snapshot = kernel.ExportGoalSnapshot(goal.Id);
        if (invalid) snapshot = snapshot with
        {
            CriterionEvidenceObligations = [Assert.Single(snapshot.CriterionEvidenceObligations!) with { Phase = (CriterionEvidencePhase)99 }]
        };
        var restored = AgentOrchestratorKernel.FromSnapshot(new OrchestratorSnapshot([snapshot], [])).GetGoal(goal.Id);
        Assert.Single(restored.GetLandingPrerequisiteObligations(Candidate));
        Assert.Empty(restored.OutstandingPostLandingDuties);
        if (invalid) Assert.Contains("malformed persisted", Assert.Single(restored.CriterionEvidenceObligations).Provenance);
    }

    [Theory]
    [InlineData("missing-operator")]
    [InlineData("conflicting-acceptance")]
    public void Restore_InvalidDeclarationOwnership_RemainsBlocking(string corruption)
    {
        var (kernel, goal, _) = Create();
        var snapshot = kernel.ExportGoalSnapshot(goal.Id);
        var original = Assert.Single(snapshot.CriterionEvidenceObligations!);
        var corrupted = snapshot with
        {
            RefinedSpecVersions = snapshot.RefinedSpecVersions!.Select(version => version with
            {
                Spec = corruption == "missing-operator"
                    ? version.Spec with { OperatorOwnedAcceptanceCriteria = [] }
                    : version.Spec with { AcceptanceGateOwnedAcceptanceCriteria = [original.Criterion] }
            }).ToArray()
        };

        var restored = AgentOrchestratorKernel.FromSnapshot(new OrchestratorSnapshot([corrupted], [])).GetGoal(goal.Id);

        var blocking = Assert.Single(restored.GetLandingPrerequisiteObligations(Candidate));
        Assert.Equal(original.Id, blocking.Id);
        Assert.Equal(CriterionEvidenceOwner.Unknown, blocking.Owner);
        Assert.Equal(CriterionEvidenceState.Pending, blocking.State);
        Assert.Contains("malformed persisted", blocking.Provenance);
        Assert.Empty(restored.OutstandingPostLandingDuties);
        Assert.Single(Reload(AgentOrchestratorKernel.FromSnapshot(new OrchestratorSnapshot([corrupted], [])))
            .GetGoal(goal.Id).GetLandingPrerequisiteObligations(Candidate));
    }

    [Fact]
    public void Refinement_EvidencedPhaseMutation_LeavesOriginalAuthority()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Preserve phase");
        kernel.RecordGoalRefinement(goal.Id, Spec() with { CriterionEvidenceDeclarations = [] });
        var duty = Assert.Single(goal.CriterionEvidenceObligations);
        kernel.RecordCriterionEvidence(goal.Id, duty.Id, duty.Owner, Candidate, "pre", duty.RequiredScope, true, "Pre-landing proof");
        Assert.Throws<InvalidOperationException>(() => kernel.RecordGoalRefinement(goal.Id, Spec()));
        Assert.Equal(CriterionEvidencePhase.PreLanding, Assert.Single(goal.CriterionEvidenceObligations).Phase);
        Assert.Single(goal.RefinedSpecVersions);
    }

    [Theory]
    [InlineData("unpublished")]
    [InlineData("missing")]
    [InlineData("old-observation")]
    [InlineData("equal-time")]
    [InlineData("foreign-artifact")]
    [InlineData("foreign-target")]
    [InlineData("unattested")]
    [InlineData("future")]
    public void Receipt_InvalidActivationOrChronology_LeavesDutyPending(string scenario)
    {
        var (kernel, goal, clock) = Create();
        var publication = Publication(goal.Id, clock.UtcNow);
        if (scenario != "unpublished") kernel.BindPostLandingPublication(goal.Id, publication);
        clock.Advance(TimeSpan.FromMinutes(5));
        var observation = Observation(publication);
        observation = scenario switch
        {
            "old-observation" => observation with { ObservedAt = publication.PromotedAt.AddSeconds(-1) },
            "equal-time" => observation with { ObservedAt = observation.ActivatedAt },
            "foreign-artifact" => observation with { PromotedMainRevision = new string('d', 40) },
            "foreign-target" => observation with { Scope = Scope with { Target = "other-runtime" } },
            "unattested" => observation with { ActivationAttested = false },
            "future" => observation with { ObservedAt = clock.UtcNow.AddDays(1) },
            _ => observation
        };
        var duty = Assert.Single(goal.OutstandingPostLandingDuties);
        Assert.Throws<InvalidOperationException>(() => kernel.RecordCriterionEvidence(goal.Id, duty.Id, duty.Owner,
            Candidate, "observation", duty.RequiredScope, true, "Observed",
            scenario == "missing" ? null : observation, "operator"));
        Assert.Equal(CriterionEvidenceState.Pending, Assert.Single(goal.OutstandingPostLandingDuties).State);
    }

    [Fact]
    public void Restore_EvidencedPhaseMutation_RemainsBlocking()
    {
        var (kernel, goal, clock) = Create();
        var publication = Publication(goal.Id, clock.UtcNow);
        kernel.BindPostLandingPublication(goal.Id, publication);
        clock.Advance(TimeSpan.FromMinutes(5));
        var duty = Assert.Single(goal.OutstandingPostLandingDuties);
        kernel.RecordCriterionEvidence(goal.Id, duty.Id, duty.Owner, Candidate, "recorded", duty.RequiredScope,
            true, "Observed", Observation(publication), "operator");
        var snapshot = kernel.ExportGoalSnapshot(goal.Id);
        var corrupted = snapshot with
        {
            RefinedSpecVersions = snapshot.RefinedSpecVersions!.Select(version => version with
            {
                Spec = version.Spec with { CriterionEvidenceDeclarations = [] }
            }).ToArray(),
            CriterionEvidenceObligations = [Assert.Single(goal.CriterionEvidenceObligations) with
            {
                Phase = CriterionEvidencePhase.PreLanding, ObservationScope = null
            }]
        };
        var restored = AgentOrchestratorKernel.FromSnapshot(new OrchestratorSnapshot([corrupted], [])).GetGoal(goal.Id);
        Assert.Equal(CriterionEvidenceOwner.Unknown, Assert.Single(restored.GetLandingPrerequisiteObligations(Candidate)).Owner);
        Assert.Empty(restored.OutstandingPostLandingDuties);
    }

    [Fact]
    public void Receipt_OriginalSupersededDuty_ClosesWithHistoryAndRejectsReplayChanges()
    {
        var (kernel, goal, clock) = Create();
        var publication = Publication(goal.Id, clock.UtcNow);
        kernel.BindPostLandingPublication(goal.Id, publication);
        clock.Advance(TimeSpan.FromHours(1));
        kernel.RecordGoalRefinement(goal.Id, new RefinedSpec("Next increment", ["Next proof"], VerificationClass.TestVerifiable, [], []));
        var duty = Assert.Single(goal.OutstandingPostLandingDuties);
        Assert.Equal(1, duty.CriterionVersion);
        Assert.Throws<InvalidOperationException>(() => kernel.RecordCriterionEvidence(goal.Id, duty.Id, duty.Owner,
            new string('b', 40), "new-artifact", duty.RequiredScope, true, "Observed", Observation(publication), "operator"));
        kernel.RecordCriterionEvidence(goal.Id, duty.Id, duty.Owner, Candidate, "red", duty.RequiredScope, false,
            "Condition absent", Observation(publication), "operator");
        var accepted = kernel.RecordCriterionEvidence(goal.Id, duty.Id, duty.Owner, Candidate, "green", duty.RequiredScope, true,
            "Condition observed", Observation(publication), "operator");
        Assert.Equal(accepted, kernel.RecordCriterionEvidence(goal.Id, duty.Id, duty.Owner, Candidate, "green", duty.RequiredScope, true,
            "Condition observed", Observation(publication), "operator"));
        Assert.Throws<InvalidOperationException>(() => kernel.RecordCriterionEvidence(goal.Id, duty.Id, duty.Owner, Candidate,
            "green", duty.RequiredScope, true, "Condition observed", Observation(publication) with { ActivationIdentity = "other-deployment" }, "operator"));
        var restored = Reload(kernel).GetGoal(goal.Id);
        Assert.Empty(restored.OutstandingPostLandingDuties);
        var original = Assert.Single(restored.CriterionEvidenceObligations);
        Assert.Equal(CriterionEvidenceState.Satisfied, original.State);
        Assert.Equal(publication, original.Publication);
        Assert.Equal("operator", original.ObservationActor);
        Assert.Equal("red", Assert.Single(original.PriorReceipts!).ReceiptId);
        Assert.Equal(Observation(publication), original.Observation);
    }

    private static (AgentOrchestratorKernel Kernel, Goal Goal, FakeClock Clock) Create()
    {
        var clock = new FakeClock();
        var kernel = new AgentOrchestratorKernel(clock);
        var goal = kernel.CreateGoal("Observe after landing");
        kernel.RecordGoalRefinement(goal.Id, Spec());
        return (kernel, goal, clock);
    }

    private static RefinedSpec Spec() => new("Observe", ["Observe the activated behavior"], VerificationClass.RealWorldDependent, [], [])
    {
        OperatorOwnedAcceptanceCriteria = ["Observe the activated behavior"],
        CriterionEvidenceDeclarations = [new("Observe the activated behavior", CriterionEvidencePhase.PostLanding, Scope, Atomic: true)]
    };

    private static GoalPromotionFact Publication(GoalId id, DateTimeOffset at) =>
        new(id.Value, Candidate, new string('b', 40), new string('c', 40), at, GoalPromotionFact.SoloProvenance);
    private static CriterionPostLandingObservation Observation(GoalPromotionFact publication) =>
        new(publication.PromotedMainRevision, Scope, "runtime-session-1", "Operator verified the running artifact and performed the observation",
            publication.PromotedAt.AddMinutes(1), publication.PromotedAt.AddMinutes(2), ActivationAttested: true,
            GoalId: publication.GoalId, ObligationId: "criterion-v1-0");
    private static AgentOrchestratorKernel Reload(AgentOrchestratorKernel kernel) =>
        AgentOrchestratorKernel.FromSnapshot(JsonSerializer.Deserialize<OrchestratorSnapshot>(JsonSerializer.Serialize(kernel.ExportSnapshot()))!);
}
