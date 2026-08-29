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
            var contextDirectory = WriteRegistry(root);
            var reviewer = new TaskSpec(TaskId.New(), "Review", AgentRole.Reviewer);
            var kernel = new AgentOrchestratorKernel();
            var goal = kernel.CreateGoal("Project canonical history", [reviewer]);
            var candidateSha = new string('a', 40);
            var findingTemplates = Enumerable.Range(0, 22).Select(index =>
            {
                var location = new ReviewFindingLocation($"src/File{index:D2}.cs", $"Type{index:D2}.Run");
                var receiptId = $"receipt-{index % 9:D2}";
                return new ReviewFinding(
                    $"stable-{index:D2}",
                    ReviewFindingState.Open,
                    location,
                    string.Empty,
                    index % 2 == 0 ? FindingSeverity.Blocking : FindingSeverity.Advisory,
                    index % 2 == 0 ? FindingCategory.Correctness : FindingCategory.TestEvidence,
                    new FindingEvidenceRequest([new FindingEvidenceSelection("tests/Tests.csproj", $"Tests.Case{index:D2}")]),
                    new FindingEvidenceOutcome(true, receiptId));
            }).ToArray();
            var receipts = Enumerable.Range(0, 9).Select(index => new FindingEvidenceReceipt(
                $"receipt-{index:D2}",
                candidateSha,
                new FindingEvidenceRequest([new FindingEvidenceSelection("tests/Tests.csproj", $"Tests.Receipt{index:D2}")]),
                true,
                true,
                $"receipt-body-{index:D2}"))
                .ToArray();

            for (var round = 0; round < 20; round++)
            {
                var contentRound = Math.Min(round, 18);
                var findings = findingTemplates.Select((finding, index) => finding with
                {
                    State = index < 14 || round < 10 ? ReviewFindingState.Open : ReviewFindingState.Resolved,
                    Description = $"decision-input-{index:D2}-round-{contentRound:D2}-" +
                                  new string((char)('a' + index % 20), 1_450),
                    Location = finding.Location with { Hunk = $"round-{contentRound:D2}" }
                }).ToArray();
                var touched = round == 10
                    ? findings.Where(finding => finding.State == ReviewFindingState.Resolved)
                        .Select(finding => finding.Location with { Hunk = "resolution-proof" }).ToArray()
                    : [];
                kernel.RecordTaskVerification(goal.Id, reviewer.Id, new TaskVerificationRecord(
                    "review",
                    root,
                    0,
                    "pass",
                    string.Empty,
                    DateTimeOffset.Parse("2026-08-01T00:00:00Z").AddMinutes(round),
                    ReviewFindingTouchedAnchors: touched,
                    ReviewedCommit: candidateSha,
                    MergedReviewFindings: findings,
                    FindingEvidenceReceipts: receipts,
                    FullStandardOutput: "pass",
                    FullStandardError: string.Empty));
            }

            var snapshots = reviewer.VerificationHistory.Select(verification => new
            {
                Findings = verification.MergedReviewFindings,
                EvidenceReceipts = verification.FindingEvidenceReceipts
            }).ToArray();
            var legacyBytes = JsonSerializer.SerializeToUtf8Bytes(snapshots);
            Assert.True(legacyBytes.Length >= 640_080, $"Production-shaped legacy fixture was only {legacyBytes.Length} bytes.");
            var exactRecordBytes = JsonSerializer.SerializeToUtf8Bytes(
                WorkerContextPackageBuilder.DistinctBySerializedValue(snapshots));
            Assert.True(exactRecordBytes.Length > 60_000,
                $"Exact-record dedupe unexpectedly reached the canonical ceiling at {exactRecordBytes.Length} bytes.");

            var package = WorkerProfileDispatcher.BuildContextPackage(
                goal,
                reviewer,
                root,
                contextDirectory,
                Brief(goal, reviewer),
                currentCandidateSha: candidateSha);
            var history = Assert.Single(package.Artifacts, artifact => artifact.Identity.Value == "goal/review-finding-history.json");
            Assert.Equal(ContextDeliveryMode.InlineFull, history.DeliveryMode);
            var ledgerBytes = Recover(root, history);
            Assert.True(ledgerBytes.Length <= 60_000, $"Canonical inline ledger was {ledgerBytes.Length} bytes.");
            using var ledger = JsonDocument.Parse(ledgerBytes);
            var projected = ledger.RootElement.GetProperty("findings").EnumerateArray().ToArray();
            Assert.Equal(22, projected.Length);
            Assert.Equal(14, projected.Count(finding => finding.GetProperty("state").GetString() == "Open"));
            Assert.Equal(8, projected.Count(finding => finding.GetProperty("state").GetString() == "Resolved"));
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
            Assert.Equal(20, roundReferences.Length);
            Assert.Equal(9, receiptReferences.Length);
            Assert.All(roundReferences.Concat(receiptReferences), reference =>
            {
                var identity = reference.GetProperty("logical_identity").GetString()!;
                var body = Assert.Single(package.Artifacts, artifact => artifact.Identity.Value == identity);
                Assert.Equal(ContextDeliveryMode.MandatoryFile, body.DeliveryMode);
                var recovered = Recover(root, body);
                Assert.Equal(reference.GetProperty("sha256").GetString(), WorkerContextArtifact.Hash(recovered));
            });
            var packageReceipt = WorkerContextPackageBuilder.CreateReceipt(package);
            Assert.Equal(19, packageReceipt.UniqueReviewFindingRoundCount);
            Assert.Equal(1, packageReceipt.DuplicateReviewFindingRoundCount);
            Assert.Equal(9, packageReceipt.UniqueFindingEvidenceReceiptCount);
            Assert.Equal(171, packageReceipt.DuplicateFindingEvidenceReceiptCount);
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
                currentCandidateSha: candidateSha);
            Assert.Equal(ReviewFindingHistoryProjectionMode.FullInspection, ordinary.ReviewFindingProjection!.Mode);
            Assert.DoesNotContain(ordinary.Artifacts,
                artifact => artifact.Identity.Value == "task/review-contract-repair-envelope.json");
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
        var finding = Assert.Single(ledger.RootElement.GetProperty("findings").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, finding.GetProperty("description").ValueKind);
        var proof = Assert.Single(finding.GetProperty("resolved_anchor_proof").EnumerateArray());
        Assert.Equal("proof-hunk", proof.GetProperty("hunk").GetString());
        var resolvingRound = finding.GetProperty("round").GetProperty("sha256").GetString();
        var indexedRounds = ledger.RootElement.GetProperty("rounds").EnumerateArray().ToArray();
        Assert.Equal(
            indexedRounds[1].GetProperty("body").GetProperty("sha256").GetString(),
            resolvingRound);
        Assert.NotEqual(
            indexedRounds[2].GetProperty("body").GetProperty("sha256").GetString(),
            resolvingRound);
        Assert.Equal("touched-anchor", finding.GetProperty("resolution_proof").GetProperty("kind").GetString());
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
            var findings = ledger.RootElement.GetProperty("findings").EnumerateArray()
                .ToDictionary(finding => finding.GetProperty("stable_id").GetString()!, StringComparer.Ordinal);
            var receiptProof = findings["receipt-resolved"].GetProperty("resolution_proof");
            Assert.Equal("candidate-bound-evidence-receipt", receiptProof.GetProperty("kind").GetString());
            Assert.Equal(candidateSha, receiptProof.GetProperty("candidate_sha").GetString());
            Assert.False(string.IsNullOrWhiteSpace(
                receiptProof.GetProperty("receipt_body").GetProperty("sha256").GetString()));
            var advisoryProof = findings["advisory-resolved"].GetProperty("resolution_proof");
            Assert.Equal("advisory-disposition", advisoryProof.GetProperty("kind").GetString());
            Assert.Equal(candidateSha, advisoryProof.GetProperty("candidate_sha").GetString());
            Assert.All(findings.Values, finding =>
                Assert.Empty(finding.GetProperty("resolved_anchor_proof").EnumerateArray()));
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
            Assert.Equal(2, package.ReviewFindingProjection.UniqueRoundCount);
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
        _ => throw new InvalidOperationException()
    };
}
