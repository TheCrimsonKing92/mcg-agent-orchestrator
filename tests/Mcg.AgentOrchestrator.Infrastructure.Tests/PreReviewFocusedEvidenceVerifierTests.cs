using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection(TestCollections.GoalAcceptanceVerifier)]
public sealed class PreReviewFocusedEvidenceVerifierTests : GoalAcceptanceVerifierTestBase
{
    [Xunit.Fact(DisplayName = "PreReviewFocusedEvidenceVerifier_accepts_mapped_project_and_safe_exclusion")]
    public async Task AcceptsMappedProjectAndSafeExclusion()
    {
        var calls = new List<string[]>();
        var root = CreateManifestWorkspace("""
            {
              "version": 1,
              "checks": [],
              "forbiddenChangedPathGlobs": []
            }
            """);
        try
        {
            var verifier = new GoalAcceptanceVerifier((args, _, _) =>
            {
                calls.Add(args);
                if (IsMtpExecutableCall(args, "Mcg.AgentOrchestrator.Core.Tests") ||
                    IsMtpExecutableCall(args, "Mcg.AgentOrchestrator.Infrastructure.Tests"))
                {
                    WriteMtpTrx(args);
                    return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(
                        0,
                        "Passed! - Failed: 0, Passed: 1, Skipped: 0, Total: 1."));
                }

                return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "Build succeeded."));
            });

            var result = await verifier.RunFocusedEvidenceAsync(
                root,
                new GoalId("abcdef12abcdef12abcdef12abcdef12"),
                "Core.Tests: mapped-project; Infrastructure.Tests: FullyQualifiedName~DashboardHostTests&Category!=HostIntegration");

            Assert.True(result.Accepted);
            Assert.True(result.Passed);
            var coreCall = calls.Single(call => IsMtpExecutableCall(call, "Mcg.AgentOrchestrator.Core.Tests"));
            Assert.DoesNotContain("--filter-class", coreCall);
            var infrastructureCall = calls.Single(call =>
                IsMtpExecutableCall(call, "Mcg.AgentOrchestrator.Infrastructure.Tests"));
            AssertArgumentPair(infrastructureCall, "--filter-class", "*DashboardHostTests*");
            AssertArgumentPair(infrastructureCall, "--filter-not-trait", "Category=HostIntegration");
            Assert.DoesNotContain(calls, call =>
                call.Length >= 3 &&
                call[0] == "dotnet" &&
                call[1] == "test" &&
                call[2].EndsWith(".sln", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            DeleteDirectoryWithRetry(root);
        }
    }

    [Xunit.Fact(DisplayName = "PreReviewFocusedEvidenceVerifier_still_rejects_unbounded_all")]
    public async Task StillRejectsUnboundedAll()
    {
        var calls = new List<string[]>();
        var root = CreateManifestWorkspace("""
            {
              "version": 1,
              "checks": [],
              "forbiddenChangedPathGlobs": []
            }
            """);
        try
        {
            var verifier = new GoalAcceptanceVerifier((args, _, _) =>
            {
                calls.Add(args);
                return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "should not run"));
            });

            var result = await verifier.RunFocusedEvidenceAsync(
                root,
                new GoalId("abcdef12abcdef12abcdef12abcdef12"),
                "Infrastructure.Tests: all");

            Assert.False(result.Accepted);
            Assert.False(result.Passed);
            Assert.Contains("unbounded evidence request rejected", result.Summary);
            Assert.Empty(result.Checks);
            Assert.Empty(calls);
        }
        finally
        {
            DeleteDirectoryWithRetry(root);
        }
    }

    [Xunit.Fact(DisplayName = "PreReviewFocusedEvidenceVerifier_runs_every_selected_check_after_red")]
    public async Task RunsEverySelectedCheckAfterRed()
    {
        var calls = new List<string[]>();
        var root = CreateManifestWorkspace("""
            {
              "version": 1,
              "checks": [],
              "forbiddenChangedPathGlobs": []
            }
            """);
        try
        {
            var verifier = new GoalAcceptanceVerifier((args, _, _) =>
            {
                calls.Add(args);
                if (IsMtpExecutableCall(args, "Mcg.AgentOrchestrator.Core.Tests"))
                {
                    WriteMtpTrx(args, "Mcg.Tests.CoreFailure", "Failed");
                    return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(1, "Failed: 1."));
                }

                if (IsMtpExecutableCall(args, "Mcg.AgentOrchestrator.Infrastructure.Tests"))
                {
                    WriteMtpTrx(args, "Mcg.Tests.InfrastructurePass", "Passed");
                    return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "Passed: 1."));
                }

                return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "Build succeeded."));
            });

            var result = await verifier.RunFocusedEvidenceAsync(
                root,
                new GoalId("abcdef12abcdef12abcdef12abcdef12"),
                "Core.Tests: FullyQualifiedName~CoreTests; Infrastructure.Tests: FullyQualifiedName~InfrastructureTests");

            Assert.True(result.Accepted);
            Assert.False(result.Passed);
            Assert.Equal(2, result.Checks.Count);
            Assert.Contains(result.Checks, check => check.FailingTestIdentities.Contains("Mcg.Tests.CoreFailure"));
            Assert.Contains(calls, call => IsMtpExecutableCall(call, "Mcg.AgentOrchestrator.Infrastructure.Tests"));
        }
        finally
        {
            DeleteDirectoryWithRetry(root);
        }
    }

    [Xunit.Fact(DisplayName = "PreReviewFocusedEvidenceVerifier_typed_trx_identity_preserves_theory_colons")]
    public void TypedTrxIdentityPreservesTheoryColons()
    {
        var root = CreateManifestWorkspace("""
            {
              "version": 1,
              "checks": [],
              "forbiddenChangedPathGlobs": []
            }
            """);
        try
        {
            var trxPath = Path.Combine(root, "theory.trx");
            File.WriteAllText(
                trxPath,
                """
                <TestRun>
                  <Results>
                    <UnitTestResult
                      testId="test-1"
                      testName="Mcg.Tests.Theory(value: 42)"
                      outcome="Failed" />
                  </Results>
                </TestRun>
                """);

            var identities = GoalAcceptanceVerifier.ExtractTrxFailureIdentities(trxPath);

            Assert.Equal(["Mcg.Tests.Theory(value: 42)"], identities);
        }
        finally
        {
            DeleteDirectoryWithRetry(root);
        }
    }

    private static bool IsMtpExecutableCall(string[] args, string projectName) =>
        args.Length > 0 &&
        Path.GetFileNameWithoutExtension(args[0]).Equals(projectName, StringComparison.OrdinalIgnoreCase);

    private static void AssertArgumentPair(string[] args, string option, string value)
    {
        for (var index = 0; index < args.Length - 1; index++)
        {
            if (args[index].Equals(option, StringComparison.Ordinal) &&
                args[index + 1].Equals(value, StringComparison.Ordinal))
            {
                return;
            }
        }

        Assert.Fail($"Expected {option} {value}.");
    }

    private static void WriteMtpTrx(
        string[] args,
        string testName = "Mcg.Tests.PassingTest",
        string outcome = "Passed")
    {
        var resultsDirectoryIndex = Array.IndexOf(args, "--results-directory");
        var trxFileIndex = Array.IndexOf(args, "--report-trx-filename");
        Assert.True(resultsDirectoryIndex >= 0);
        Assert.True(resultsDirectoryIndex + 1 < args.Length);
        Assert.True(trxFileIndex >= 0);
        Assert.True(trxFileIndex + 1 < args.Length);
        Directory.CreateDirectory(args[resultsDirectoryIndex + 1]);
        File.WriteAllText(
            Path.Combine(args[resultsDirectoryIndex + 1], args[trxFileIndex + 1]),
            $"""
             <TestRun>
               <Results>
                 <UnitTestResult testId="test-1" testName="{testName}" outcome="{outcome}" />
               </Results>
             </TestRun>
             """);
    }
}
