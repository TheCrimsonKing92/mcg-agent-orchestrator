using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using static LandingExecutorTests;

// Owns a temporary repository; shares the LandingExecutor git seam's serial collection.
[Xunit.Collection(TestCollections.LandingGitRunner)]
public sealed class LandingExecutorTestsLandingPolicyDecision
{
    [Theory]
    [InlineData("acceptance", 2, "acceptance-not-accepted")]
    [InlineData("ownership", 4, "ownership-hold")]
    [InlineData("promote", 0, "landing-proceed")]
    public void CandidateOutcome_PreservesResultFieldsAndRecordsLandingFacts(string scenario, int rung, string evidence)
    {
        var repo = CreateGitRepository();
        try
        {
            const string changedFile = "src/landing-policy.txt";
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            AgentOrchestratorKernel kernel;
            Goal goal;
            string? approvalPath = null;
            string? developerId = null;
            if (scenario == "ownership")
            {
                var setup = CreateVerifiedOwnershipApprovalGoal(repo);
                kernel = setup.Kernel;
                goal = setup.Goal;
                approvalPath = setup.ApprovalPath;
                developerId = setup.Developer.Id.Value;
            }
            else if (scenario == "promote")
            {
                var setup = CreateAcceptedCandidate(repo, changedFile);
                workspace = setup.Workspace;
                kernel = setup.Kernel;
                goal = setup.Goal;
            }
            else
            {
                (kernel, goal) = CreateVerifiedGoal(repo);
                AddGoalBranchCommit(repo, GoalWorktrees.BranchName(goal.Id), changedFile, "goal work");
                GoalWorktrees.Ensure(repo, goal.Id);
            }
            var accepted = GoalAcceptanceStatusProjector.Build(kernel, goal, repo);
            Assert.Equal(scenario != "acceptance", accepted.IsAccepted);
            var mainBefore = ReadGit(repo, "rev-parse", "main");
            var branch = GoalWorktrees.BranchName(goal.Id);
            var expectedReason = scenario switch
            {
                "acceptance" => $"no passed acceptance outcome for candidate {ReadGit(repo, "rev-parse", branch)[..8].ToLowerInvariant()}",
                "ownership" => "ownership-denylist hold: 1 task(s) touched RequiresOperatorApproval path(s)",
                _ => "Landing pre-mutation checks passed."
            };
            if (scenario == "acceptance")
                Assert.Equal(expectedReason, accepted.AcceptanceHoldDescription);

            var result = LandingExecutor.Execute(kernel, goal, workspace, policy: ConductorAutonomyPolicy.Conservative);

            Assert.Equal(goal.Id.Value, result.GoalId);
            Assert.Equal(goal.Id.Value[..8], result.GoalPrefix);
            Assert.Equal("integration", result.IntegrationBranch);
            Assert.Equal(scenario == "promote", result.MainAdvanced);
            Assert.Equal(scenario == "promote"
                ? $"Promoted: {branch} prepared from bound main via integration into main."
                : $"Parked on integration: {expectedReason}", result.Message);
            if (scenario == "promote")
            {
                Assert.IsType<LandingDecision.Promote>(result.Decision);
                Assert.Equal(ReadGit(repo, "rev-parse", "main"), result.MergeCommitSha);
                Assert.NotEqual(mainBefore, result.MergeCommitSha);
                Assert.Equal(new[] { changedFile }, result.ChangedFiles);
                Assert.Equal(result.MergeCommitSha, ReadGit(repo, "rev-parse", "integration"));
            }
            else
            {
                Assert.Equal(expectedReason, Assert.IsType<LandingDecision.Escalate>(result.Decision).Reason);
                Assert.Null(result.MergeCommitSha);
                Assert.Null(result.ChangedFiles);
                Assert.Equal(mainBefore, ReadGit(repo, "rev-parse", "main"));
                Assert.False(GitCli.Run(repo, "rev-parse", "--verify", "--quiet", "refs/heads/integration").Succeeded);
            }
            var decision = Assert.IsType<PolicyDecisionRecord>(result.Decision.Decision);
            Assert.Equal("landing", decision.Stage);
            Assert.Equal(scenario == "promote" ? "Proceed" : "Escalate", decision.Action);
            Assert.Equal(rung, decision.Rung);
            Assert.Equal(evidence, decision.DiscriminatingEvidence);
            Assert.Equal(expectedReason, decision.Reason);
            Assert.Equal(10, decision.Facts.Count);
            AssertFact(decision, "changedFilesResolved", "true");
            AssertFact(decision, "changedFilesFailureReason", "");
            AssertFact(decision, "acceptanceAccepted", scenario == "acceptance" ? "false" : "true");
            AssertFact(decision, "acceptanceHoldDescription", accepted.AcceptanceHoldDescription ?? "");
            AssertFact(decision, "evidenceRebindOutstanding", scenario == "acceptance" ? "" : "false");
            AssertFact(decision, "evidenceDiagnostic", "");
            AssertFact(decision, "ownershipRequiresApproval", scenario == "acceptance" ? "" : scenario == "ownership" ? "true" : "false");
            AssertFact(decision, "allowsAutonomousHighRiskOwnership", scenario == "acceptance" ? "" : "false");
            AssertFact(decision, "attributableHoldRequestCount", scenario == "ownership" ? "1" : "");
            AssertFact(decision, "integrationAncestry", scenario == "promote" ? "absent" : "");
            var replay = LandingPolicy.Evaluate(LandingFacts.FromRecordedFacts(decision.Facts));
            Assert.Equal(rung, replay.DiscriminatingRung);
            Assert.Equal(expectedReason, replay.Reason);

            var inbox = OperatorInbox.Build(kernel, [], WorkerProfileCatalog.Default(), workspace, goal.Id.Value[..8]);
            if (scenario == "ownership")
            {
                var hold = Assert.Single(inbox.Items, item => item.Kind == OperatorInboxKind.OwnershipHold);
                Assert.Equal(developerId, hold.TaskId);
                Assert.Contains(approvalPath!, hold.Evidence);
                Assert.DoesNotContain(inbox.Items, item => item.Kind == OperatorInboxKind.LandingEscalation);
            }
            else if (scenario == "acceptance")
            {
                Assert.Single(inbox.Items, item => item.Kind == OperatorInboxKind.LandingEscalation);
                Assert.DoesNotContain(inbox.Items, item => item.Kind == OperatorInboxKind.OwnershipHold);
            }
            else
            {
                Assert.DoesNotContain(inbox.Items, item => item.Kind is OperatorInboxKind.OwnershipHold or OperatorInboxKind.LandingEscalation);
            }
        }
        finally
        {
            TryDeleteDirectory(repo);
        }
    }

    private static void AssertFact(PolicyDecisionRecord decision, string name, string value) =>
        Assert.Equal(value, Assert.Single(decision.Facts, fact => fact.Name == name).Value);
}
