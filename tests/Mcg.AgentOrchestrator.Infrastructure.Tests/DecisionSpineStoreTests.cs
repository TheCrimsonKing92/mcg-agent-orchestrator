using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class DecisionSpineStoreTests
{
    [Fact(DisplayName = "DecisionSpine_typed_records_round_trip_with_opaque_action_refs")]
    public async Task TypedRecordsRoundTripWithOpaqueActionRefs()
    {
        var store = new CollaborationItemStore(DbPath());
        var request = Request();

        await store.RaiseDecisionRequestAsync(request);
        await store.RecordNotificationDeliveryAsync(new NotificationDelivery(
            "delivery-1", request.Id, "discord", "operator-board", "sha256-card", request.CreatedAt));

        var listed = await store.ListDecisionRequestsAsync("goal-1");
        var state = await store.GetDecisionStateAsync(request.Id);

        Assert.Single(listed);
        Assert.NotNull(state);
        Assert.Equal(DecisionRequestPhase.Requested, state!.Phase);
        Assert.Equal(request.EvidenceManifest.ManifestHash, listed[0].EvidenceManifest.ManifestHash);
        Assert.Equal("act_retry", listed[0].AllowedActions[1].ActionRef.Value);
        Assert.DoesNotContain(
            typeof(DecisionAllowedAction).GetProperties(),
            property => property.Name.Equals("Command", StringComparison.Ordinal));
    }

    [Fact(DisplayName = "DecisionSpine_rejects_wire_borne_command_strings_as_action_refs")]
    public async Task RejectsWireBorneCommandStringsAsActionRefs()
    {
        var store = new CollaborationItemStore(DbPath());
        var request = Request(actions:
        [
            new DecisionAllowedAction(
                new DecisionActionRef("retry goal-1"),
                "Retry",
                DecisionActionKind.Retry,
                AuthorizationTier.Mutate,
                Now.AddHours(1),
                7)
        ]);

        var error = await Assert.ThrowsAsync<ArgumentException>(() => store.RaiseDecisionRequestAsync(request));

        Assert.Contains("opaque tokens", error.Message);
    }

    [Fact(DisplayName = "DecisionSpine_rejects_mutated_evidence_manifest_hashes")]
    public async Task RejectsMutatedEvidenceManifestHashes()
    {
        var store = new CollaborationItemStore(DbPath());
        var manifest = new EvidenceManifest(
            [new EvidenceManifestEntry("receipt-1", "sha256-good")],
            "sha256-mutated");
        var request = Request(manifest: manifest);

        var error = await Assert.ThrowsAsync<ArgumentException>(() => store.RaiseDecisionRequestAsync(request));

        Assert.Contains("Evidence manifest hash", error.Message);
    }

    [Fact(DisplayName = "DecisionSpine_expiry_records_fail_closed_default_and_never_approval")]
    public async Task ExpiryRecordsFailClosedDefaultAndNeverApproval()
    {
        var store = new CollaborationItemStore(DbPath());
        var request = Request(kind: DecisionRequestKind.RiskApproval, defaultDisposition: DecisionDefaultDisposition.Deny);
        await store.RaiseDecisionRequestAsync(request);

        var receipt = await store.RecordExpiredDefaultDispositionAsync(request.Id, request.ExpiresAt.AddSeconds(1));
        var state = await store.GetDecisionStateAsync(request.Id);
        var approvalDefault = Request(id: "decision-approve", defaultDisposition: DecisionDefaultDisposition.Approve);
        var riskOpenDefault = Request(id: "decision-risk-open", kind: DecisionRequestKind.RiskApproval, defaultDisposition: DecisionDefaultDisposition.NoAction);

        Assert.Equal("system:expiry", receipt.ActorId);
        Assert.Equal("default:Deny", receipt.Response.Value);
        Assert.Equal(DecisionRequestPhase.DecisionRecorded, state!.Phase);
        await Assert.ThrowsAsync<ArgumentException>(() => store.RaiseDecisionRequestAsync(approvalDefault));
        await Assert.ThrowsAsync<ArgumentException>(() => store.RaiseDecisionRequestAsync(riskOpenDefault));
    }

    [Fact(DisplayName = "DecisionSpine_decision_recorded_is_distinct_from_effect_applied_and_idempotent")]
    public async Task DecisionRecordedDistinctFromEffectAppliedAndIdempotent()
    {
        var store = new CollaborationItemStore(DbPath());
        var request = Request();
        await store.RaiseDecisionRequestAsync(request);

        var receipt = await store.RecordDecisionAsync(
            request.Id,
            "discord:miles",
            "discord",
            AuthorizationTier.Mutate,
            7,
            new DecisionResponse(new DecisionActionRef("act_retry"), "retry", DecisionReuseScope.ThisGoal, false),
            Now.AddMinutes(1));
        var duplicateReceipt = await store.RecordDecisionAsync(
            request.Id,
            "discord:miles",
            "discord",
            AuthorizationTier.Mutate,
            7,
            new DecisionResponse(new DecisionActionRef("act_retry"), "retry again", DecisionReuseScope.ThisGoal, false),
            Now.AddMinutes(2));
        var recordedOnly = await store.GetDecisionStateAsync(request.Id);
        var applied = await store.TryApplyDecisionEffectAsync(
            request.Id, receipt.Id, new DecisionActionRef("act_retry"), 7, "retry queued", Now.AddMinutes(4));
        var duplicateApply = await store.TryApplyDecisionEffectAsync(
            request.Id, receipt.Id, new DecisionActionRef("act_retry"), 7, "retry queued twice", Now.AddMinutes(5));
        var final = await store.GetDecisionStateAsync(request.Id);

        var staleRequest = Request(id: "decision-stale");
        await store.RaiseDecisionRequestAsync(staleRequest);
        var staleReceipt = await store.RecordDecisionAsync(
            staleRequest.Id,
            "discord:miles",
            "discord",
            AuthorizationTier.Mutate,
            7,
            new DecisionResponse(new DecisionActionRef("act_retry"), "retry", DecisionReuseScope.ThisGoal, false),
            Now.AddMinutes(1));
        var stale = await store.TryApplyDecisionEffectAsync(
            staleRequest.Id, staleReceipt.Id, new DecisionActionRef("act_retry"), 8, "retry applied", Now.AddMinutes(3));
        var stillRecorded = await store.GetDecisionStateAsync(staleRequest.Id);

        Assert.Equal(receipt.Id, duplicateReceipt.Id);
        Assert.Equal(DecisionRequestPhase.DecisionRecorded, recordedOnly!.Phase);
        Assert.Null(recordedOnly.Effect);
        Assert.True(applied.Applied);
        Assert.True(duplicateApply.Duplicate);
        Assert.Equal(applied.Receipt.Id, duplicateApply.Receipt.Id);
        Assert.Equal(DecisionRequestPhase.EffectApplied, final!.Phase);
        Assert.False(stale.Applied);
        Assert.Equal("Stale goal state version.", stale.ErrorMessage);
        Assert.Equal(DecisionRequestPhase.DecisionRecorded, stillRecorded!.Phase);
    }

    [Fact(DisplayName = "DecisionSpine_records_reuse_scope_and_flags_permanent_policy_without_applying_it")]
    public async Task RecordsReuseScopeAndFlagsPermanentPolicyWithoutApplyingIt()
    {
        var store = new CollaborationItemStore(DbPath());
        var request = Request();
        await store.RaiseDecisionRequestAsync(request);

        var receipt = await store.RecordDecisionAsync(
            request.Id,
            "discord:miles",
            "discord",
            AuthorizationTier.Answer,
            null,
            new DecisionResponse(new DecisionActionRef("act_deny"), "deny", DecisionReuseScope.ProposePermanentPolicy, false),
            Now.AddMinutes(1));
        var state = await store.GetDecisionStateAsync(request.Id);

        Assert.True(receipt.Response.PermanentPolicyProposed);
        Assert.Equal(DecisionReuseScope.ProposePermanentPolicy, receipt.Response.SelectedReuseScope);
        Assert.Equal(DecisionRequestPhase.DecisionRecorded, state!.Phase);
        Assert.Null(state.Effect);
    }

    [Fact(DisplayName = "DecisionSpine_auth_tiers_reject_higher_actions_and_classify_listed_actions")]
    public async Task AuthTiersRejectHigherActionsAndClassifyListedActions()
    {
        Assert.Equal(AuthorizationTier.Answer, DecisionAuthorization.RequiredTierFor(DecisionActionKind.Clarify));
        Assert.Equal(AuthorizationTier.Answer, DecisionAuthorization.RequiredTierFor(DecisionActionKind.Deny));
        Assert.Equal(AuthorizationTier.Answer, DecisionAuthorization.RequiredTierFor(DecisionActionKind.Park));
        Assert.Equal(AuthorizationTier.Mutate, DecisionAuthorization.RequiredTierFor(DecisionActionKind.Retry));
        Assert.Equal(AuthorizationTier.Mutate, DecisionAuthorization.RequiredTierFor(DecisionActionKind.Recover));
        Assert.Equal(AuthorizationTier.Mutate, DecisionAuthorization.RequiredTierFor(DecisionActionKind.Unpark));
        Assert.Equal(AuthorizationTier.Mutate, DecisionAuthorization.RequiredTierFor(DecisionActionKind.Intake));
        Assert.Equal(AuthorizationTier.AttestLand, DecisionAuthorization.RequiredTierFor(DecisionActionKind.VerifyManual));
        Assert.Equal(AuthorizationTier.AttestLand, DecisionAuthorization.RequiredTierFor(DecisionActionKind.RiskyLanding));
        Assert.Equal(AuthorizationTier.AttestLand, DecisionAuthorization.RequiredTierFor(DecisionActionKind.DenylistChange));

        var store = new CollaborationItemStore(DbPath());
        var request = Request();
        await store.RaiseDecisionRequestAsync(request);
        var receipt = await store.RecordDecisionAsync(
            request.Id,
            "discord:miles",
            "discord",
            AuthorizationTier.Answer,
            7,
            new DecisionResponse(new DecisionActionRef("act_retry"), "retry", DecisionReuseScope.ThisOccurrence, false),
            Now.AddMinutes(1));

        var result = await store.TryApplyDecisionEffectAsync(
            request.Id, receipt.Id, new DecisionActionRef("act_retry"), 7, "retry queued", Now.AddMinutes(2));

        Assert.False(result.Applied);
        Assert.Contains("does not satisfy required tier", result.ErrorMessage);
    }

    private static readonly DateTimeOffset Now = new(2026, 7, 20, 12, 0, 0, TimeSpan.Zero);

    private static DecisionRequest Request(
        string id = "decision-1",
        DecisionRequestKind kind = DecisionRequestKind.General,
        DecisionDefaultDisposition defaultDisposition = DecisionDefaultDisposition.Deny,
        EvidenceManifest? manifest = null,
        IReadOnlyList<DecisionAllowedAction>? actions = null) =>
        new(
            id,
            kind,
            "goal-1",
            "Operator decision",
            "Rendered text shown to Miles",
            "decision-template:v1",
            manifest ?? EvidenceManifest.Create([new EvidenceManifestEntry("receipt-1", "sha256-evidence")]),
            Now.AddHours(1),
            defaultDisposition,
            new DecisionBlockingImpact("dispatch queue is blocked", ["goal-1:developer"]),
            [
                DecisionReuseScope.ThisOccurrence,
                DecisionReuseScope.ThisGoal,
                DecisionReuseScope.ThisForkClass,
                DecisionReuseScope.ProposePermanentPolicy
            ],
            actions ??
            [
                new DecisionAllowedAction(new DecisionActionRef("act_deny"), "Deny", DecisionActionKind.Deny, AuthorizationTier.Answer, Now.AddHours(1), null),
                new DecisionAllowedAction(new DecisionActionRef("act_retry"), "Retry", DecisionActionKind.Retry, AuthorizationTier.Mutate, Now.AddHours(1), 7),
                new DecisionAllowedAction(new DecisionActionRef("act_verify"), "Verify manual", DecisionActionKind.VerifyManual, AuthorizationTier.AttestLand, Now.AddHours(1), 7)
            ],
            Now);

    private static string DbPath() =>
        Path.Combine(CreateTempDirectory(), "collab-decisions.db");
}
