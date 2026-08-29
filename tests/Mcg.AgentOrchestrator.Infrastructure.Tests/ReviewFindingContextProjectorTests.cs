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
            var findings = Enumerable.Range(0, 22).Select(index =>
            {
                var location = new ReviewFindingLocation($"src/File{index:D2}.cs", $"Type{index:D2}.Run");
                var receiptId = $"receipt-{index % 9:D2}";
                return new ReviewFinding(
                    $"stable-{index:D2}",
                    index < 14 ? ReviewFindingState.Open : ReviewFindingState.Resolved,
                    location,
                    $"decision-input-{index:D2}-" + new string((char)('a' + index % 20), 1_450),
                    index % 2 == 0 ? FindingSeverity.Blocking : FindingSeverity.Advisory,
                    index % 2 == 0 ? FindingCategory.Correctness : FindingCategory.TestEvidence,
                    new FindingEvidenceRequest([new FindingEvidenceSelection("tests/Tests.csproj", $"Tests.Case{index:D2}")]),
                    new FindingEvidenceOutcome(true, receiptId));
            }).ToArray();
            var touched = findings.Where(finding => finding.State == ReviewFindingState.Resolved)
                .Select(finding => finding.Location).ToArray();
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

            var legacyBytes = JsonSerializer.SerializeToUtf8Bytes(reviewer.VerificationHistory.Select(verification => new
            {
                Findings = verification.MergedReviewFindings,
                EvidenceReceipts = verification.FindingEvidenceReceipts
            }));
            Assert.True(legacyBytes.Length >= 640_080, $"Production-shaped legacy fixture was only {legacyBytes.Length} bytes.");

            var package = WorkerProfileDispatcher.BuildContextPackage(
                goal,
                reviewer,
                root,
                contextDirectory,
                Brief(goal, reviewer),
                currentCandidateSha: candidateSha);
            var history = Assert.Single(package.Artifacts, artifact => artifact.Identity.Value == "goal/review-finding-history.json");
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
            var packageReceipt = WorkerContextPackageBuilder.CreateReceipt(package);
            Assert.Equal(1, packageReceipt.UniqueReviewFindingRoundCount);
            Assert.Equal(19, packageReceipt.DuplicateReviewFindingRoundCount);
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
                FullStandardOutput: "malformed structured output",
                FullStandardError: string.Empty));
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

    private static TaskBrief Brief(Goal goal, TaskSpec task) => new(
        goal.Id,
        task.Id,
        task.RequiredRole,
        task.Description,
        $"# Agent Task Brief{Environment.NewLine}Goal: {goal.Objective}{Environment.NewLine}## Instructions{Environment.NewLine}Review.");

    private static byte[] Recover(string root, WorkerContextArtifact artifact) => artifact.DeliveryMode switch
    {
        ContextDeliveryMode.InlineFull => artifact.AuthoritativeBytes!,
        ContextDeliveryMode.MandatoryFile => File.ReadAllBytes(Path.Combine(root, artifact.MandatoryRelativePath!.Replace('/', Path.DirectorySeparatorChar))),
        _ => throw new InvalidOperationException()
    };
}
