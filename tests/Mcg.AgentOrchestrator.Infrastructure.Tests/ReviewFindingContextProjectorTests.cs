using System.Text;
using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class ReviewFindingContextProjectorTests
{
    [Xunit.Fact]
    public void ProductionShapedHistoryRetainsDecisionInputsWithinInlineCeilingAndInternsBodies()
    {
        var root = CreateRoot();
        try
        {
            var production = WriteProductionRegistry(root);
            var contextDirectory = production.ContextDirectory;
            var reviewer = new TaskSpec(TaskId.New(), "Review", AgentRole.Reviewer);
            var kernel = new AgentOrchestratorKernel();
            var goal = kernel.CreateGoal("Project canonical history", [reviewer]);
            kernel.SetGoalRefinedSpec(goal.Id, new RefinedSpec(
                "Project only active retry evidence.",
                ["criterion-a", "criterion-b"],
                VerificationClass.TestVerifiable,
                [],
                []));
            kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
            var candidateSha = new string('a', 40);
            var baseSha = new string('b', 40);
            var resolvedLocation = new ReviewFindingLocation("src/Resolved.cs", "Resolved.Run");
            var activeALocation = new ReviewFindingLocation("src/ActiveA.cs", "ActiveA.Run");
            var activeBLocation = new ReviewFindingLocation("src/ActiveB.cs", "ActiveB.Run");
            var requestA = new FindingEvidenceRequest([new FindingEvidenceSelection("tests/Tests.csproj", "Tests.ActiveA")]);
            var requestB = new FindingEvidenceRequest([new FindingEvidenceSelection("tests/Tests.csproj", "Tests.ActiveB")]);
            var receiptA = new FindingEvidenceReceipt("receipt-a", candidateSha, requestA, true, true, "receipt-a-body");
            var receiptB = new FindingEvidenceReceipt("receipt-b", candidateSha, requestB, true, true, "receipt-b-body");
            var roundFindings = new[]
            {
                new[]
                {
                    Finding("eventually-resolved", resolvedLocation, "ROUND0_RESOLVED_SENTINEL_" + new string('r', 1_450)),
                    Finding("active-a", activeALocation, "ROUND0_SUPERSEDED_SENTINEL_" + new string('s', 1_450), requestA, "receipt-a")
                },
                new[]
                {
                    Finding("active-b", activeBLocation, "ROUND1_ACTIVE_SENTINEL_" + new string('t', 1_450) + "_ACTIVE_B_ACTIONABLE_SUFFIX", requestB, "receipt-b")
                },
                new[]
                {
                    Finding("active-a", activeALocation, "ROUND2_ACTIVE_SENTINEL_" + new string('u', 1_450) + "_ACTIVE_A_ACTIONABLE_SUFFIX", requestA, "receipt-a")
                },
                new[]
                {
                    Finding("eventually-resolved", resolvedLocation, "ROUND3_RESOLUTION_SENTINEL_" + new string('v', 1_450)) with
                    {
                        State = ReviewFindingState.Resolved
                    }
                }
            };
            var roundReceipts = new IReadOnlyList<FindingEvidenceReceipt>[]
            {
                [receiptA],
                [receiptB],
                [receiptA],
                []
            };
            for (var round = 0; round < roundFindings.Length; round++)
            {
                kernel.RecordTaskVerification(goal.Id, reviewer.Id, new TaskVerificationRecord(
                    "review",
                    root,
                    0,
                    "pass",
                    string.Empty,
                    DateTimeOffset.Parse("2026-08-01T00:00:00Z").AddMinutes(round),
                    ReviewFindingTouchedAnchors: round == 3 ? [resolvedLocation] : [],
                    ReviewedCommit: candidateSha,
                    MergedReviewFindings: roundFindings[round],
                    FindingEvidenceReceipts: roundReceipts[round],
                    FullStandardOutput: "pass",
                    FullStandardError: string.Empty));
            }
            kernel.RecordOperatorTaskNote(
                goal.Id,
                reviewer.Id,
                "CRITERIA CORRECTION: supersedes=\"criterion-a\"; correction=\"OBSOLETE_CORRECTION_SENTINEL\"");
            kernel.RecordOperatorTaskNote(
                goal.Id,
                reviewer.Id,
                "CRITERIA CORRECTION: supersedes=\"criterion-a\"; correction=\"NEWEST_CORRECTION_SENTINEL\"");
            kernel.WaiveAcceptanceCriterion(goal.Id, "criterion-b", "WAIVER_SENTINEL");
            kernel.RetryTask(
                goal.Id,
                reviewer.Id,
                "CRITERIA CORRECTION: newest correction is authoritative.",
                retryRoundKind: RetryRoundKind.Mechanical);

            var snapshots = reviewer.VerificationHistory.Select(verification => new
            {
                Findings = verification.MergedReviewFindings,
                EvidenceReceipts = verification.FindingEvidenceReceipts
            }).ToArray();
            var legacyHistory = JsonSerializer.Serialize(snapshots);
            var legacyTimeline = JsonSerializer.Serialize(goal.Timeline);
            var legacyCorrections = JsonSerializer.Serialize(goal.EffectiveAcceptanceCriteriaCorrections);
            var legacyPrompt = string.Join(
                Environment.NewLine,
                production.Policy,
                production.SourceSurvey,
                production.PriorTaskEvidence,
                legacyHistory,
                legacyTimeline,
                legacyCorrections);
            var legacyDeliveredArtifactBytes = Encoding.UTF8.GetByteCount(
                production.Policy + production.SourceSurvey + production.PriorTaskEvidence + legacyHistory + legacyTimeline + legacyCorrections);
            var legacyTranscript = string.Concat(
                legacyPrompt,
                production.SourceSurvey,
                production.SourceSurvey,
                production.PriorTaskEvidence,
                legacyHistory,
                legacyTimeline);
            Assert.True(legacyPrompt.Length >= 84_089, $"Production-shaped legacy prompt was only {legacyPrompt.Length} characters.");

            var package = WorkerProfileDispatcher.BuildContextPackage(
                goal,
                reviewer,
                root,
                contextDirectory,
                Brief(goal, reviewer),
                currentCandidateSha: candidateSha,
                comparisonBaseSha: baseSha,
                workerProfile: Assert.Single(WorkerProfileCatalog.Default().Profiles, profile => profile.Name == "codex-cli"));
            var history = Assert.Single(package.Artifacts, artifact => artifact.Identity.Value == "goal/review-finding-history.json");
            Assert.Equal(ContextDeliveryMode.InlineFull, history.DeliveryMode);
            var ledgerBytes = Recover(root, history);
            Assert.True(ledgerBytes.Length <= 60_000, $"Canonical inline ledger was {ledgerBytes.Length} bytes.");
            using var ledger = JsonDocument.Parse(ledgerBytes);
            Assert.Equal(candidateSha, ledger.RootElement.GetProperty("current_candidate_sha").GetString());
            Assert.Equal(baseSha, ledger.RootElement.GetProperty("comparison_base_sha").GetString());
            var projected = ledger.RootElement.GetProperty("findings").EnumerateArray().ToArray();
            Assert.Equal(2, projected.Length);
            Assert.All(projected, finding => Assert.Equal("Open", finding.GetProperty("state").GetString()));
            Assert.All(projected, finding =>
            {
                Assert.Equal(candidateSha, finding.GetProperty("candidate_sha").GetString());
                Assert.False(string.IsNullOrWhiteSpace(finding.GetProperty("verdict_identity").GetString()));
                Assert.False(string.IsNullOrWhiteSpace(finding.GetProperty("evidence_identity").GetString()));
            });
            var roundReferences = ledger.RootElement.GetProperty("rounds").EnumerateArray()
                .Select(round => round.GetProperty("body"))
                .ToArray();
            var receiptReferences = ledger.RootElement.GetProperty("receipt_bodies").EnumerateArray().ToArray();
            Assert.Equal(2, roundReferences.Length);
            Assert.Equal(2, receiptReferences.Length);
            var corrections = ledger.RootElement.GetProperty("effective_operator_corrections").EnumerateArray().ToArray();
            Assert.Equal(2, corrections.Length);
            Assert.Contains(corrections, correction => correction.GetProperty("Correction").GetString() == "NEWEST_CORRECTION_SENTINEL");
            Assert.Contains(corrections, correction => correction.GetProperty("Correction").GetString()!.Contains("WAIVER_SENTINEL", StringComparison.Ordinal));
            Assert.DoesNotContain(corrections, correction => correction.GetProperty("Correction").GetString() == "OBSOLETE_CORRECTION_SENTINEL");
            Assert.DoesNotContain(package.Artifacts, artifact =>
                artifact.DeliveryMode == ContextDeliveryMode.MandatoryFile &&
                (artifact.Identity.Value.StartsWith("goal/review-finding-rounds/", StringComparison.Ordinal) ||
                 artifact.Identity.Value.StartsWith("goal/review-finding-receipts/", StringComparison.Ordinal)));
            Assert.All(roundReferences.Concat(receiptReferences), reference =>
            {
                var identity = reference.GetProperty("logical_identity").GetString()!;
                var body = Assert.Single(package.Artifacts, artifact => artifact.Identity.Value == identity);
                Assert.Equal(ContextDeliveryMode.OnDemandFile, body.DeliveryMode);
                var recovered = Recover(root, body);
                Assert.Equal(reference.GetProperty("sha256").GetString(), WorkerContextArtifact.Hash(recovered));
            });
            var rendered = WorkerContextPackageBuilder.Render(package);
            var compactTranscript = rendered;
            var packageReceipt = WorkerContextPackageBuilder.CreateReceipt(package)
                .WithToolTranscriptCharacters(compactTranscript.Length)
                .WithBaselineMeasurements(legacyPrompt.Length, legacyDeliveredArtifactBytes, legacyTranscript.Length);
            Assert.Equal(2, packageReceipt.UniqueReviewFindingRoundCount);
            Assert.Equal(2, packageReceipt.DuplicateReviewFindingRoundCount);
            Assert.Equal(2, packageReceipt.UniqueFindingEvidenceReceiptCount);
            Assert.Equal(1, packageReceipt.DuplicateFindingEvidenceReceiptCount);
            Assert.True(rendered.Length <= 21_022,
                $"Compact prompt {rendered.Length} chars exceeded the 21,022-character production ceiling.");
            Assert.True(rendered.Length * 4 <= packageReceipt.BaselinePromptCharacters,
                $"Compact prompt {rendered.Length} chars did not reduce the measured {legacyPrompt.Length}-character fixture baseline by 4x.");
            Assert.True((rendered.Length + compactTranscript.Length) * 4 <= legacyPrompt.Length + legacyTranscript.Length);
            Assert.DoesNotContain("ROUND0_RESOLVED_SENTINEL", rendered, StringComparison.Ordinal);
            Assert.DoesNotContain("ROUND0_SUPERSEDED_SENTINEL", rendered, StringComparison.Ordinal);
            Assert.DoesNotContain("ROUND3_RESOLUTION_SENTINEL", rendered, StringComparison.Ordinal);
            Assert.DoesNotContain("OBSOLETE_CORRECTION_SENTINEL", rendered, StringComparison.Ordinal);
            Assert.Contains("ROUND1_ACTIVE_SENTINEL", rendered, StringComparison.Ordinal);
            Assert.Contains("ROUND2_ACTIVE_SENTINEL", rendered, StringComparison.Ordinal);
            Assert.Contains("ACTIVE_A_ACTIONABLE_SUFFIX", rendered, StringComparison.Ordinal);
            Assert.Contains("ACTIVE_B_ACTIONABLE_SUFFIX", rendered, StringComparison.Ordinal);
            var retryBrief = Assert.Single(package.Artifacts, artifact => artifact.Identity.Value == "brief/current.md");
            Assert.Contains("## Instructions", Encoding.UTF8.GetString(Recover(root, retryBrief)), StringComparison.Ordinal);
            Assert.Contains(package.Artifacts, artifact => artifact.Identity.Value == "task/criterion-retry-feedback.json");
            Assert.Contains(package.Artifacts, artifact => artifact.Identity.Value == "goal/timeline.json");
            Assert.Contains("NEWEST_CORRECTION_SENTINEL", rendered, StringComparison.Ordinal);
            Assert.Contains("WAIVER_SENTINEL", rendered, StringComparison.Ordinal);
            Assert.Equal(rendered.Length, packageReceipt.RenderedPromptCharacters);
            Assert.Equal(Encoding.UTF8.GetByteCount(rendered), packageReceipt.RenderedPromptBytes);
            Assert.Equal(compactTranscript.Length, packageReceipt.ToolTranscriptCharacters);
            Assert.Equal(legacyTranscript.Length, packageReceipt.BaselineToolTranscriptCharacters);
            Assert.Equal(legacyPrompt.Length, packageReceipt.BaselinePromptCharacters);
            Assert.Equal(legacyDeliveredArtifactBytes, packageReceipt.BaselineDeliveredArtifactBytes);
            Assert.True(packageReceipt.ModelInputTokenEstimate * 4 <= packageReceipt.BaselineModelInputTokenEstimate);
            Assert.Equal(WorkerPromptInputBudget.CountTokens(rendered), packageReceipt.ModelInputTokenEstimate);
            Assert.True(packageReceipt.DeliveredArtifactBytes > 0);
            Assert.True(packageReceipt.OnDemandArtifactBytes > packageReceipt.RenderedPromptBytes);
            var historical = package.Artifacts.Where(artifact => artifact.DeliveryMode == ContextDeliveryMode.HistoricalFile).ToArray();
            Assert.NotEmpty(historical);
            Assert.All(historical, artifact => Assert.DoesNotContain(artifact.Identity.Value, rendered, StringComparison.Ordinal));

            kernel.RecordTaskVerification(goal.Id, reviewer.Id, new TaskVerificationRecord(
                "review",
                root,
                0,
                "pass",
                string.Empty,
                DateTimeOffset.Parse("2026-08-01T00:10:00Z"),
                ReviewFindingTouchedAnchors: [new ReviewFindingLocation("src/Historical.cs", "Historical.Run")],
                ReviewedCommit: candidateSha,
                MergedReviewFindings:
                [
                    Finding(
                        "historical-only",
                        new ReviewFindingLocation("src/Historical.cs", "Historical.Run"),
                        "EXTRA_RESOLVED_HISTORY_SENTINEL_" + new string('z', 1_450)) with
                    {
                        State = ReviewFindingState.Resolved
                    }
                ],
                FullStandardOutput: "pass",
                FullStandardError: string.Empty));
            kernel.RetryTask(
                goal.Id,
                reviewer.Id,
                "CRITERIA CORRECTION: newest correction remains authoritative.",
                retryRoundKind: RetryRoundKind.Mechanical);
            var replayed = WorkerProfileDispatcher.BuildContextPackage(
                goal,
                reviewer,
                root,
                contextDirectory,
                Brief(goal, reviewer),
                currentCandidateSha: candidateSha,
                comparisonBaseSha: baseSha,
                workerProfile: Assert.Single(WorkerProfileCatalog.Default().Profiles, profile => profile.Name == "codex-cli"));
            var replayedRendered = WorkerContextPackageBuilder.Render(replayed);
            var replayedLedger = Assert.Single(
                replayed.Artifacts,
                artifact => artifact.Identity.Value == "goal/review-finding-history.json");
            Assert.Equal(ledgerBytes, Recover(root, replayedLedger));
            Assert.DoesNotContain("EXTRA_RESOLVED_HISTORY_SENTINEL", replayedRendered, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Fact]
    public void CrossRoleCarryForwardKeepsOneCanonicalLatestEntry()
    {
        var root = CreateRoot();
        try
        {
            var contextDirectory = WriteRegistry(root);
            var tester = new TaskSpec(TaskId.New(), "Test", AgentRole.Tester);
            var reviewer = new TaskSpec(TaskId.New(), "Review", AgentRole.Reviewer);
            var kernel = new AgentOrchestratorKernel();
            var goal = kernel.CreateGoal("Reconcile cross-role finding history", [tester, reviewer]);
            var candidateSha = new string('a', 40);
            var location = new ReviewFindingLocation("src/Shared.cs", "Shared.Run");
            kernel.RecordTaskVerification(goal.Id, tester.Id, Verification(
                DateTimeOffset.Parse("2026-08-01T00:00:00Z"), candidateSha,
                [new ReviewFinding("shared-stable-id", ReviewFindingState.Open, location, "tester description")]));
            kernel.RecordTaskVerification(goal.Id, reviewer.Id, Verification(
                DateTimeOffset.Parse("2026-08-01T00:01:00Z"), candidateSha,
                [new ReviewFinding("shared-stable-id", ReviewFindingState.Open, location, "reviewer description")]));

            var package = WorkerProfileDispatcher.BuildContextPackage(
                goal,
                reviewer,
                root,
                contextDirectory,
                Brief(goal, reviewer),
                currentCandidateSha: candidateSha);

            var history = Assert.Single(package.Artifacts,
                artifact => artifact.Identity.Value == "goal/review-finding-history.json");
            using var ledger = JsonDocument.Parse(Recover(root, history));
            var finding = Assert.Single(ledger.RootElement.GetProperty("findings").EnumerateArray());
            Assert.Equal("shared-stable-id", finding.GetProperty("stable_id").GetString());
            Assert.Equal((int)AgentRole.Reviewer, finding.GetProperty("role").GetInt32());
            Assert.Equal("reviewer description", finding.GetProperty("description").GetString());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Fact]
    public void SameCandidateContractRepairUsesCompactAllowListWhileCandidateChangeFallsBackFull()
    {
        var root = CreateRoot();
        try
        {
            var contextDirectory = WriteRegistry(root);
            var reviewer = new TaskSpec(TaskId.New(), "Review", AgentRole.Reviewer);
            var kernel = new AgentOrchestratorKernel();
            var goal = kernel.CreateGoal("Repair output contract", [reviewer]);
            var candidateSha = new string('b', 40);
            var finding = new ReviewFinding(
                "stable-one",
                ReviewFindingState.Open,
                new ReviewFindingLocation("src/One.cs", "One.Run"),
                "substantive finding");
            var receipts = new List<FindingEvidenceReceipt>
            {
                new(
                    "receipt-one",
                    candidateSha,
                    new FindingEvidenceRequest([new FindingEvidenceSelection("tests/Tests.csproj", "Tests.One")]),
                    true,
                    true,
                    "evidence unchanged")
            };
            kernel.RecordTaskVerification(goal.Id, reviewer.Id, new TaskVerificationRecord(
                "review",
                root,
                1,
                "malformed structured output",
                string.Empty,
                DateTimeOffset.Parse("2026-08-01T00:00:00Z"),
                ReviewFindingTouchedAnchors: [finding.Location],
                ReviewedCommit: candidateSha,
                MergedReviewFindings: [finding],
                ReviewFindingContractViolation: new ReviewFindingContractViolation("schema-invalid", "findings line was malformed"),
                FindingEvidenceReceipts: receipts,
                FullStandardOutput: "malformed structured output",
                FullStandardError: string.Empty,
                CompletionVerdictRule: "substantive-needs-work"));
            var baselineFull = WorkerProfileDispatcher.BuildContextPackage(
                goal,
                reviewer,
                root,
                contextDirectory,
                Brief(goal, reviewer),
                currentCandidateSha: candidateSha);
            var baselineLedger = Assert.Single(baselineFull.Artifacts,
                artifact => artifact.Identity.Value == "goal/review-finding-history.json");
            using var baselineDocument = JsonDocument.Parse(Recover(root, baselineLedger));
            var baselineFindings = baselineDocument.RootElement.GetProperty("findings").GetRawText();
            kernel.RetryTask(goal.Id, reviewer.Id, "Repair only the structured output contract.", retryRoundKind: RetryRoundKind.Mechanical);

            var compact = WorkerProfileDispatcher.BuildContextPackage(
                goal,
                reviewer,
                root,
                contextDirectory,
                Brief(goal, reviewer),
                currentCandidateSha: candidateSha);
            Assert.Contains(compact.Artifacts, artifact => artifact.Identity.Value == "task/review-contract-repair-envelope.json");
            Assert.DoesNotContain(compact.Artifacts, artifact => artifact.Identity.Value == "goal/objective.md");
            Assert.DoesNotContain(compact.Artifacts, artifact => artifact.Identity.Value == "brief/current.md");
            Assert.Equal(ReviewFindingHistoryProjectionMode.ContractRepair, compact.ReviewFindingProjection!.Mode);
            var envelopeArtifact = Assert.Single(compact.Artifacts,
                artifact => artifact.Identity.Value == "task/review-contract-repair-envelope.json");
            using var envelope = JsonDocument.Parse(Recover(root, envelopeArtifact));
            Assert.Equal(baselineFindings, envelope.RootElement.GetProperty("findings").GetRawText());
            Assert.Equal("substantive-needs-work", envelope.RootElement.GetProperty("prior_substantive_verdict").GetString());

            var full = WorkerProfileDispatcher.BuildContextPackage(
                goal,
                reviewer,
                root,
                contextDirectory,
                Brief(goal, reviewer),
                currentCandidateSha: new string('c', 40));
            Assert.DoesNotContain(full.Artifacts, artifact => artifact.Identity.Value == "task/review-contract-repair-envelope.json");
            Assert.Contains(full.Artifacts, artifact => artifact.Identity.Value == "goal/objective.md");
            Assert.Equal("candidate-changed", full.ReviewFindingProjection!.FallbackReason);

            receipts[0] = receipts[0] with { Summary = "material evidence changed" };
            var changedEvidence = WorkerProfileDispatcher.BuildContextPackage(
                goal,
                reviewer,
                root,
                contextDirectory,
                Brief(goal, reviewer),
                currentCandidateSha: candidateSha);
            Assert.DoesNotContain(changedEvidence.Artifacts,
                artifact => artifact.Identity.Value == "task/review-contract-repair-envelope.json");
            Assert.Equal("material-evidence-changed", changedEvidence.ReviewFindingProjection!.FallbackReason);

            var ordinaryReviewer = new TaskSpec(TaskId.New(), "Review normally", AgentRole.Reviewer);
            var ordinaryGoal = kernel.CreateGoal("Mechanical verification only", [ordinaryReviewer]);
            kernel.RecordTaskVerification(ordinaryGoal.Id, ordinaryReviewer.Id, new TaskVerificationRecord(
                "review",
                root,
                0,
                "pass",
                string.Empty,
                DateTimeOffset.Parse("2026-08-01T00:01:00Z"),
                ReviewedCommit: candidateSha,
                MergedReviewFindings: [finding],
                FullStandardOutput: "pass",
                FullStandardError: string.Empty));
            kernel.RetryTask(ordinaryGoal.Id, ordinaryReviewer.Id, "Rerun verification only.", retryRoundKind: RetryRoundKind.Mechanical);
            var ordinary = WorkerProfileDispatcher.BuildContextPackage(
                ordinaryGoal,
                ordinaryReviewer,
                root,
                contextDirectory,
                Brief(ordinaryGoal, ordinaryReviewer),
                currentCandidateSha: candidateSha,
                comparisonBaseSha: new string('b', 40));
            Assert.Equal(ReviewFindingHistoryProjectionMode.FullInspection, ordinary.ReviewFindingProjection!.Mode);
            Assert.DoesNotContain(ordinary.Artifacts,
                artifact => artifact.Identity.Value == "task/review-contract-repair-envelope.json");
            Assert.Contains(ordinary.Artifacts, artifact => artifact.Identity.Value == "goal/objective.md");
            Assert.Contains(ordinary.Artifacts, artifact => artifact.Identity.Value == "task/criterion-retry-feedback.json");
            Assert.Contains(ordinary.Artifacts, artifact => artifact.Identity.Value == "goal/timeline.json");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Fact]
    public void ResolvedTombstoneRetainsResolvingRoundAndEquivalentAnchorProof()
    {
        var reviewer = new TaskSpec(TaskId.New(), "Review", AgentRole.Reviewer);
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Preserve resolution provenance", [reviewer]);
        var candidateSha = new string('d', 40);
        var location = new ReviewFindingLocation("src/One.cs", "One.Run", "finding-hunk");
        var open = new ReviewFinding("stable-one", ReviewFindingState.Open, location, "open finding");
        var resolved = open with { State = ReviewFindingState.Resolved, Description = "resolved detail" };
        kernel.RecordTaskVerification(goal.Id, reviewer.Id, Verification(
            DateTimeOffset.Parse("2026-08-01T00:00:00Z"), candidateSha, [open]));
        kernel.RecordTaskVerification(goal.Id, reviewer.Id, Verification(
            DateTimeOffset.Parse("2026-08-01T00:01:00Z"), candidateSha, [resolved],
            [location with { Hunk = "proof-hunk" }]));
        kernel.RecordTaskVerification(goal.Id, reviewer.Id, Verification(
            DateTimeOffset.Parse("2026-08-01T00:02:00Z"), candidateSha,
            [resolved with { Description = "carried forward without replayed proof" }]));

        var projection = ReviewFindingContextProjector.Project(goal, reviewer, candidateSha);
        using var ledger = JsonDocument.Parse(projection.LedgerBytes);
        Assert.Empty(ledger.RootElement.GetProperty("findings").EnumerateArray());
        Assert.Empty(ledger.RootElement.GetProperty("rounds").EnumerateArray());
        Assert.Equal(3, projection.RoundBodies.Count);
        Assert.Contains(projection.RoundBodies, body =>
        {
            using var round = JsonDocument.Parse(body.Bytes);
            return round.RootElement.GetProperty("TouchedAnchors").EnumerateArray()
                .Any(anchor => anchor.GetProperty("hunk").GetString() == "proof-hunk");
        });
    }

    [Xunit.Fact]
    public void NoTouchReceiptAndAdvisoryResolutionsPrepareNextDispatchWithExplicitProof()
    {
        var root = CreateRoot();
        try
        {
            var contextDirectory = WriteRegistry(root);
            var reviewer = new TaskSpec(TaskId.New(), "Review", AgentRole.Reviewer);
            var kernel = new AgentOrchestratorKernel();
            var goal = kernel.CreateGoal("Preserve valid no-touch resolutions", [reviewer]);
            var candidateSha = new string('e', 40);
            var request = new FindingEvidenceRequest(
                [new FindingEvidenceSelection("tests/Tests.csproj", "Tests.Receipt")]);
            var receipt = new FindingEvidenceReceipt(
                "receipt-proof",
                candidateSha,
                request,
                true,
                true,
                "candidate evidence passed");
            var receiptResolved = new ReviewFinding(
                "receipt-resolved",
                ReviewFindingState.Resolved,
                new ReviewFindingLocation("src/Receipt.cs", "Receipt.Run"),
                "resolved by evidence",
                FindingSeverity.Blocking,
                FindingCategory.TestEvidence,
                request,
                new FindingEvidenceOutcome(
                    true,
                    receipt.ReceiptId,
                    ResultReason: FindingEvidenceOutcomeReason.ValidEvidence));
            var advisoryResolved = new ReviewFinding(
                "advisory-resolved",
                ReviewFindingState.Resolved,
                new ReviewFindingLocation("src/Advisory.cs", "Advisory.Run"),
                "advisory disposition",
                FindingSeverity.Advisory,
                FindingCategory.CodeQuality);
            kernel.RecordTaskVerification(goal.Id, reviewer.Id, Verification(
                DateTimeOffset.Parse("2026-07-31T23:59:00Z"),
                candidateSha,
                [
                    receiptResolved with { State = ReviewFindingState.Open, EvidenceOutcome = null },
                    advisoryResolved with { State = ReviewFindingState.Open }
                ]));
            kernel.RecordTaskVerification(goal.Id, reviewer.Id, Verification(
                DateTimeOffset.Parse("2026-08-01T00:00:00Z"),
                candidateSha,
                [receiptResolved, advisoryResolved],
                receipts: [receipt]));

            var package = WorkerProfileDispatcher.BuildContextPackage(
                goal,
                reviewer,
                root,
                contextDirectory,
                Brief(goal, reviewer),
                currentCandidateSha: candidateSha);

            var rendered = WorkerContextPackageBuilder.Render(package);
            Assert.Contains("goal/review-finding-history.json", rendered, StringComparison.Ordinal);
            var ledgerArtifact = Assert.Single(package.Artifacts,
                artifact => artifact.Identity.Value == "goal/review-finding-history.json");
            using var ledger = JsonDocument.Parse(Recover(root, ledgerArtifact));
            Assert.Empty(ledger.RootElement.GetProperty("findings").EnumerateArray());
            Assert.True(ledger.RootElement.GetProperty("early_convergence_eligible").GetBoolean());
            Assert.True(package.ReviewFindingProjection!.EarlyConvergenceEligible);
            var receiptHash = WorkerContextArtifact.Hash(JsonSerializer.SerializeToUtf8Bytes(receipt));
            Assert.Contains(receiptHash, package.ReviewFindingProjection.EarlyConvergenceReceiptHashes!);
            Assert.Contains(package.Artifacts, artifact =>
                artifact.Identity.Value == $"goal/review-finding-receipts/{receiptHash}.json" &&
                artifact.DeliveryMode == ContextDeliveryMode.OnDemandFile);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Fact]
    public void ProjectionFailuresExposeTypedReasons()
    {
        var candidateSha = new string('e', 40);

        var missingTask = new TaskSpec(TaskId.New(), "Review", AgentRole.Reviewer);
        var missingKernel = new AgentOrchestratorKernel();
        var missingGoal = missingKernel.CreateGoal("Missing history", [missingTask]);
        missingKernel.RecordTaskVerification(missingGoal.Id, missingTask.Id, new TaskVerificationRecord(
            "review", ".", 1, "bad contract", string.Empty,
            DateTimeOffset.Parse("2026-08-01T00:00:00Z"),
            ReviewedCommit: candidateSha,
            ReviewFindingContractViolation: new ReviewFindingContractViolation("schema-invalid", "missing findings"),
            FullStandardOutput: "bad contract",
            FullStandardError: string.Empty));
        missingKernel.RetryTask(
            missingGoal.Id,
            missingTask.Id,
            "Repair output contract.",
            retryRoundKind: RetryRoundKind.Mechanical);
        AssertReason(
            "required-history-missing",
            () => ReviewFindingContextProjector.Project(missingGoal, missingTask, candidateSha));

        var receiptTask = new TaskSpec(TaskId.New(), "Review", AgentRole.Reviewer);
        var receiptKernel = new AgentOrchestratorKernel();
        var receiptGoal = receiptKernel.CreateGoal("Conflicting receipts", [receiptTask]);
        var request = new FindingEvidenceRequest([new FindingEvidenceSelection("tests/Tests.csproj", "Tests.One")]);
        receiptKernel.RecordTaskVerification(receiptGoal.Id, receiptTask.Id, Verification(
            DateTimeOffset.Parse("2026-08-01T00:00:00Z"), candidateSha, [],
            receipts: [new FindingEvidenceReceipt("same-id", candidateSha, request, true, true, "first body")]));
        receiptKernel.RecordTaskVerification(receiptGoal.Id, receiptTask.Id, Verification(
            DateTimeOffset.Parse("2026-08-01T00:01:00Z"), candidateSha, [],
            receipts: [new FindingEvidenceReceipt("same-id", candidateSha, request, true, true, "changed body")]));
        AssertReason(
            "evidence-identity-conflict",
            () => ReviewFindingContextProjector.Project(receiptGoal, receiptTask, candidateSha));

        var ambiguousTask = new TaskSpec(TaskId.New(), "Review", AgentRole.Reviewer);
        var ambiguousKernel = new AgentOrchestratorKernel();
        var ambiguousGoal = ambiguousKernel.CreateGoal("Ambiguous identity", [ambiguousTask]);
        var ambiguousLocation = new ReviewFindingLocation("src/Ambiguous.cs", "Ambiguous.Run");
        ambiguousKernel.RecordTaskVerification(ambiguousGoal.Id, ambiguousTask.Id, Verification(
            DateTimeOffset.Parse("2026-08-01T00:00:00Z"), candidateSha,
            [
                new ReviewFinding("same-stable-id", ReviewFindingState.Open, ambiguousLocation, "first body"),
                new ReviewFinding("same-stable-id", ReviewFindingState.Open, ambiguousLocation, "second body")
            ]));
        AssertReason(
            "stable-identity-ambiguous",
            () => ReviewFindingContextProjector.Project(ambiguousGoal, ambiguousTask, candidateSha));
    }

    [Xunit.Fact]
    public void CurrentCandidateReceiptProjectsBoundedMachineDerivedEvidenceSummary()
    {
        var reviewer = new TaskSpec(TaskId.New(), "Review", AgentRole.Reviewer);
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Project current focused evidence", [reviewer]);
        var candidateSha = new string('a', 40);
        var selections = Enumerable.Range(1, 14)
            .Select(index => new FindingEvidenceSelection("Infrastructure.Tests", $"EvidenceClass{index:D2}Tests"))
            .ToArray();
        var request = new FindingEvidenceRequest(selections);
        var requestedIdentity = string.Join('|', selections.Select(selection =>
            $"{selection.TestProject}:{selection.TestClass}"));
        var receipt = new FindingEvidenceReceipt(
            "receipt-current-summary",
            candidateSha,
            request,
            Accepted: true,
            Passed: true,
            "focused evidence passed",
            Arms:
            [
                new FindingEvidenceArmReceipt(
                    FindingEvidenceArm.Candidate,
                    candidateSha,
                    FindingEvidenceArmDisposition.Green,
                    Accepted: true,
                    Passed: true,
                    "candidate passed",
                    ReceiptPaths: [@"C:\retained\result.trx"],
                    ExecutedTestCount: 14,
                    TestResultPaths: [@"C:\retained\result.trx"])
            ],
            RequestDispositions:
            [
                new FindingEvidenceRequestDisposition(
                    "summary-finding",
                    requestedIdentity,
                    "executed-standalone")
            ],
            ExecutionBasisIdentity: "focused-v1-sha256:basis");
        var finding = new ReviewFinding(
            "summary-finding",
            ReviewFindingState.Open,
            new ReviewFindingLocation("src/Summary.cs", "Summary.Run"),
            "Current focused evidence is available.",
            FindingSeverity.Blocking,
            FindingCategory.TestEvidence,
            request,
            new FindingEvidenceOutcome(
                Honoured: true,
                ReceiptId: receipt.ReceiptId,
                ResultReason: FindingEvidenceOutcomeReason.ValidEvidence,
                RequestedSelectionIdentity: requestedIdentity,
                DecisionReason: "reused-covered-green:exact",
                SourceReceiptIds: [receipt.ReceiptId]));
        kernel.RecordTaskVerification(goal.Id, reviewer.Id, Verification(
            DateTimeOffset.Parse("2026-09-22T00:00:00Z"),
            candidateSha,
            [finding],
            receipts: [receipt]));

        var currentProjection = ReviewFindingContextProjector.Project(goal, reviewer, candidateSha);
        using var currentLedger = JsonDocument.Parse(currentProjection.LedgerBytes);
        var currentFinding = Assert.Single(currentLedger.RootElement.GetProperty("findings").EnumerateArray());
        var summary = currentFinding.GetProperty("evidence_summary");
        Assert.Equal(receipt.ReceiptId, summary.GetProperty("receipt_id").GetString());
        Assert.Equal("passed", summary.GetProperty("result").GetString());
        Assert.Equal("exact", summary.GetProperty("coverage").GetString());
        Assert.Equal(12, summary.GetProperty("requested").GetArrayLength());
        Assert.Equal(2, summary.GetProperty("requested_truncated").GetInt32());
        Assert.Equal(14, summary.GetProperty("executed_test_count").GetInt32());
        Assert.Single(currentFinding.GetProperty("receipt_bodies").EnumerateArray());

        var changedProjection = ReviewFindingContextProjector.Project(goal, reviewer, new string('b', 40));
        using var changedLedger = JsonDocument.Parse(changedProjection.LedgerBytes);
        var changedFinding = Assert.Single(changedLedger.RootElement.GetProperty("findings").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, changedFinding.GetProperty("evidence_summary").ValueKind);
    }

    [Xunit.Fact]
    public void RestoredDuplicateDurableRoundKeepsLatestEnrichmentWithoutDuplicatingRoundIndex()
    {
        var reviewer = new TaskSpec(TaskId.New(), "Review", AgentRole.Reviewer);
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Repair duplicate durable verification history", [reviewer]);
        var completedAt = DateTimeOffset.Parse("2026-08-29T10:52:21.2954854Z");
        var candidateSha = new string('a', 40);
        var request = new FindingEvidenceRequest(
            [new FindingEvidenceSelection("tests/Tests.csproj", "Tests.StableRound")]);
        var finding = new ReviewFinding(
            "stable-round",
            ReviewFindingState.Open,
            new ReviewFindingLocation("src/Stable.cs", "Stable.Run"),
            "Focused evidence is required.",
            EvidenceRequest: request);
        var receipt = new FindingEvidenceReceipt(
            "receipt-stable-round",
            candidateSha,
            request,
            Accepted: true,
            Passed: true,
            "focused evidence passed");

        kernel.RecordTaskVerification(goal.Id, reviewer.Id, Verification(
            completedAt,
            candidateSha,
            [finding]));
        kernel.RecordFindingEvidenceOutcome(
            goal.Id,
            reviewer.Id,
            finding.StableId,
            new FindingEvidenceOutcome(
                true,
                receipt.ReceiptId,
                ResultReason: FindingEvidenceOutcomeReason.ValidEvidence),
            receipt);

        var exported = kernel.ExportSnapshot();
        var goalSnapshot = Assert.Single(exported.Goals);
        var taskSnapshot = Assert.Single(goalSnapshot.Tasks);
        var enriched = Assert.IsType<TaskVerificationSnapshot>(taskSnapshot.LastVerification);
        var plain = enriched with
        {
            MergedReviewFindings = enriched.MergedReviewFindings!
                .Select(item => item with { EvidenceOutcome = null })
                .ToArray(),
            FindingEvidenceReceipts = null
        };
        var corruptTask = taskSnapshot with
        {
            LastVerification = plain,
            VerificationHistory = [plain, enriched]
        };
        var restoredKernel = AgentOrchestratorKernel.FromSnapshot(exported with
        {
            Goals = [goalSnapshot with { Tasks = [corruptTask] }]
        });
        var restoredGoal = restoredKernel.GetGoal(goal.Id);
        var restoredReviewer = restoredKernel.GetTask(goal.Id, reviewer.Id);
        Assert.Single(restoredReviewer.VerificationHistory);

        var projection = ReviewFindingContextProjector.Project(restoredGoal, restoredReviewer, candidateSha);

        Assert.Equal(1, projection.Metrics.UniqueRoundCount);
        Assert.Equal(0, projection.Metrics.DuplicateRoundCount);
        using var ledger = JsonDocument.Parse(projection.LedgerBytes);
        Assert.Single(ledger.RootElement.GetProperty("rounds").EnumerateArray());
        var projectedFinding = Assert.Single(ledger.RootElement.GetProperty("findings").EnumerateArray());
        Assert.False(string.IsNullOrWhiteSpace(projectedFinding.GetProperty("evidence_identity").GetString()));
        var receiptReference = Assert.Single(projectedFinding.GetProperty("receipt_bodies").EnumerateArray());
        Assert.Equal(
            WorkerContextArtifact.Hash(JsonSerializer.SerializeToUtf8Bytes(receipt)),
            receiptReference.GetProperty("sha256").GetString());
    }

    [Xunit.Fact]
    public void DistinctSameTimestampRoundsWithContradictoryFindingBodiesFailClosed()
    {
        var firstReviewer = new TaskSpec(TaskId.New(), "First review", AgentRole.Reviewer);
        var secondReviewer = new TaskSpec(TaskId.New(), "Second review", AgentRole.Reviewer);
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal(
            "Reject same-timestamp cross-round contradictions",
            [firstReviewer, secondReviewer]);
        var completedAt = DateTimeOffset.Parse("2026-08-29T10:52:21.2954854Z");
        var candidateSha = new string('c', 40);
        var location = new ReviewFindingLocation("src/Stable.cs", "Stable.Run");
        kernel.RecordTaskVerification(goal.Id, firstReviewer.Id, Verification(
            completedAt,
            candidateSha,
            [new ReviewFinding("same-stable-id", ReviewFindingState.Open, location, "Still open.")]));
        kernel.RecordTaskVerification(goal.Id, secondReviewer.Id, Verification(
            completedAt,
            candidateSha,
            [new ReviewFinding("same-stable-id", ReviewFindingState.Resolved, location, "Resolved.")]));

        AssertReason(
            "stable-identity-ambiguous",
            () => ReviewFindingContextProjector.Project(goal, secondReviewer, candidateSha));
    }

    [Xunit.Fact]
    public void OrdinaryBlockingResolutionWithoutTouchProofFallsBackToFullInspectionAndPreservesArtifacts()
    {
        var root = CreateRoot();
        try
        {
            var contextDirectory = WriteRegistry(root);
            var reviewer = new TaskSpec(TaskId.New(), "Review", AgentRole.Reviewer);
            var kernel = new AgentOrchestratorKernel();
            var goal = kernel.CreateGoal("Preserve well-formed resolution history", [reviewer]);
            var candidateSha = new string('e', 40);
            var location = new ReviewFindingLocation("src/Resolved.cs", "Resolved.Run");
            var unrelatedLocation = new ReviewFindingLocation("src/Unrelated.cs", "Unrelated.Run");
            var unrelatedFinding = new ReviewFinding(
                "unrelated-id",
                ReviewFindingState.Open,
                unrelatedLocation,
                "unrelated open finding");
            kernel.RecordTaskVerification(goal.Id, reviewer.Id, Verification(
                DateTimeOffset.Parse("2026-08-01T00:00:00Z"), candidateSha,
                [
                    new ReviewFinding("resolved-id", ReviewFindingState.Open, location, "open finding"),
                    unrelatedFinding
                ]));
            var resolvedFinding = new ReviewFinding(
                "resolved-id",
                ReviewFindingState.Resolved,
                location,
                "resolved finding");
            var resolvingOutput = string.Join(
                Environment.NewLine,
                $"findings: {JsonSerializer.Serialize(new[] { resolvedFinding, unrelatedFinding })}",
                $"touched_anchors: {JsonSerializer.Serialize(new[] { unrelatedLocation })}");
            kernel.RecordTaskVerification(goal.Id, reviewer.Id, new TaskVerificationRecord(
                "review",
                root,
                0,
                resolvingOutput,
                string.Empty,
                DateTimeOffset.Parse("2026-08-01T00:01:00Z"),
                ReviewFindingTouchedAnchors: [unrelatedLocation],
                ReviewedCommit: candidateSha,
                MergedReviewFindings: [resolvedFinding, unrelatedFinding],
                FullStandardOutput: resolvingOutput,
                FullStandardError: string.Empty));
            Assert.Equal(unrelatedLocation, Assert.Single(reviewer.LastVerification!.ReviewFindingTouchedAnchors!));

            var package = WorkerProfileDispatcher.BuildContextPackage(
                goal,
                reviewer,
                root,
                contextDirectory,
                Brief(goal, reviewer),
                currentCandidateSha: candidateSha);

            Assert.Equal(ReviewFindingHistoryProjectionMode.FullInspection, package.ReviewFindingProjection!.Mode);
            Assert.Equal("resolved-anchor-proof-missing", package.ReviewFindingProjection.FallbackReason);
            Assert.Contains(package.Artifacts, artifact => artifact.Identity.Value == "goal/objective.md");
            Assert.Contains(package.Artifacts, artifact => artifact.Identity.Value == "task/description.md");
            Assert.Contains(package.Artifacts, artifact => artifact.Identity.Value == "brief/current.md");
            var history = Assert.Single(package.Artifacts,
                artifact => artifact.Identity.Value == "goal/review-finding-history.json");
            using var ledger = JsonDocument.Parse(Recover(root, history));
            Assert.Equal(
                (int)ReviewFindingHistoryProjectionMode.FullInspection,
                ledger.RootElement.GetProperty("projection_mode").GetInt32());
            Assert.Equal("resolved-anchor-proof-missing", ledger.RootElement.GetProperty("fallback_reason").GetString());
            var projectedFindings = ledger.RootElement.GetProperty("findings").EnumerateArray().ToArray();
            Assert.Equal(2, projectedFindings.Length);
            var finding = Assert.Single(projectedFindings.Where(item =>
                item.GetProperty("stable_id").GetString() == "resolved-id"));
            Assert.Equal("resolved finding", finding.GetProperty("description").GetString());
            Assert.Equal(JsonValueKind.Null, finding.GetProperty("resolution_proof").ValueKind);
            Assert.Equal(1, package.ReviewFindingProjection.UniqueRoundCount);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Fact]
    public void InternedBodyHashMismatchFailsClosed()
    {
        var root = CreateRoot();
        try
        {
            var contextDirectory = WriteRegistry(root);
            var reviewer = new TaskSpec(TaskId.New(), "Review", AgentRole.Reviewer);
            var kernel = new AgentOrchestratorKernel();
            var goal = kernel.CreateGoal("Verify immutable body", [reviewer]);
            var candidateSha = new string('f', 40);
            var finding = new ReviewFinding(
                "stable-one",
                ReviewFindingState.Open,
                new ReviewFindingLocation("src/One.cs", "One.Run"),
                "finding");
            kernel.RecordTaskVerification(goal.Id, reviewer.Id, Verification(
                DateTimeOffset.Parse("2026-08-01T00:00:00Z"), candidateSha, [finding]));
            var package = WorkerProfileDispatcher.BuildContextPackage(
                goal,
                reviewer,
                root,
                contextDirectory,
                Brief(goal, reviewer),
                currentCandidateSha: candidateSha);
            var round = Assert.Single(package.Artifacts,
                artifact => artifact.Identity.Value.StartsWith("goal/review-finding-rounds/", StringComparison.Ordinal));
            File.WriteAllText(
                Path.Combine(root, round.MandatoryRelativePath!.Replace('/', Path.DirectorySeparatorChar)),
                "tampered");

            AssertReason(
                "hash-mismatch",
                () => new WorkerContextPackageBuilder().Prepare(
                    package.TargetRole,
                    root,
                    package.Artifacts,
                    allowInlineFallback: false));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateRoot()
    {
        var path = Path.Combine(Path.GetTempPath(), $"review-history-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static string WriteRegistry(string root)
    {
        var contextDirectory = Path.Combine(root, ".orchestrator-context", "goal");
        Directory.CreateDirectory(contextDirectory);
        File.WriteAllText(Path.Combine(contextDirectory, "artifact-registry.json"), JsonSerializer.Serialize(new { artifacts = Array.Empty<object>() }));
        return contextDirectory;
    }

    private static (string ContextDirectory, string Policy, string SourceSurvey, string PriorTaskEvidence)
        WriteProductionRegistry(string root)
    {
        var contextDirectory = Path.Combine(root, ".orchestrator-context", "goal");
        Directory.CreateDirectory(contextDirectory);
        var policy = "POLICY_START\n" + new string('p', 40_000) + "\nPOLICY_SENTINEL_AFTER_DEFAULT_CAP";
        var sourceSurvey = "SOURCE_SURVEY_START\n" + new string('s', 36_000) + "\nSOURCE_SURVEY_END";
        var priorTaskEvidence = "PRIOR_TASK_EVIDENCE_START\n" + new string('e', 31_000) + "\nPRIOR_TASK_EVIDENCE_END";
        var artifacts = new[]
        {
            WriteRegisteredArtifact(contextDirectory, "AGENTS.md", policy),
            WriteRegisteredArtifact(contextDirectory, "source-survey.md", sourceSurvey),
            WriteRegisteredArtifact(contextDirectory, "prior-task-evidence.md", priorTaskEvidence)
        };
        File.WriteAllText(
            Path.Combine(contextDirectory, "artifact-registry.json"),
            JsonSerializer.Serialize(new { artifacts }));
        return (contextDirectory, policy, sourceSurvey, priorTaskEvidence);
    }

    private static object WriteRegisteredArtifact(string contextDirectory, string path, string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        File.WriteAllBytes(Path.Combine(contextDirectory, path), bytes);
        return new
        {
            path,
            exists = true,
            sha256 = WorkerContextArtifact.Hash(bytes),
            roleVisibility = new[] { "Reviewer" },
            hashVerified = true
        };
    }

    private static ReviewFinding Finding(
        string stableId,
        ReviewFindingLocation location,
        string description,
        FindingEvidenceRequest? request = null,
        string? receiptId = null) => new(
            stableId,
            ReviewFindingState.Open,
            location,
            description,
            FindingSeverity.Blocking,
            FindingCategory.Correctness,
            request,
            receiptId is null ? null : new FindingEvidenceOutcome(true, receiptId));

    private static TaskVerificationRecord Verification(
        DateTimeOffset completedAt,
        string candidateSha,
        IReadOnlyList<ReviewFinding> findings,
        IReadOnlyList<ReviewFindingLocation>? touchedAnchors = null,
        IReadOnlyList<FindingEvidenceReceipt>? receipts = null) => new(
        "review",
        ".",
        0,
        "pass",
        string.Empty,
        completedAt,
        ReviewFindingTouchedAnchors: touchedAnchors,
        ReviewedCommit: candidateSha,
        MergedReviewFindings: findings,
        FindingEvidenceReceipts: receipts,
        FullStandardOutput: "pass",
        FullStandardError: string.Empty);

    private static void AssertReason(string expected, Action action)
    {
        var error = Assert.Throws<WorkerContextPreparationException>(action);
        Assert.Equal(expected, error.Reason);
    }

    private static TaskBrief Brief(Goal goal, TaskSpec task) => new(
        goal.Id,
        task.Id,
        task.RequiredRole,
        task.Description,
        $"# Agent Task Brief{Environment.NewLine}Goal: {goal.Objective}{Environment.NewLine}Goal id: {goal.Id.Value}{Environment.NewLine}## Instructions{Environment.NewLine}Review.");

    private static byte[] Recover(string root, WorkerContextArtifact artifact) => artifact.DeliveryMode switch
    {
        ContextDeliveryMode.InlineFull => artifact.AuthoritativeBytes!,
        ContextDeliveryMode.MandatoryFile => File.ReadAllBytes(Path.Combine(root, artifact.MandatoryRelativePath!.Replace('/', Path.DirectorySeparatorChar))),
        ContextDeliveryMode.OnDemandFile => File.ReadAllBytes(Path.Combine(root, artifact.MandatoryRelativePath!.Replace('/', Path.DirectorySeparatorChar))),
        ContextDeliveryMode.HistoricalFile => File.ReadAllBytes(Path.Combine(root, artifact.MandatoryRelativePath!.Replace('/', Path.DirectorySeparatorChar))),
        _ => throw new InvalidOperationException()
    };
}
