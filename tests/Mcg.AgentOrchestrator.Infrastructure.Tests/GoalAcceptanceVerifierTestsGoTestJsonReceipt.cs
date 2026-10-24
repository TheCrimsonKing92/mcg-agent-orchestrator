using System.Collections.Concurrent;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;

// Isolated for the verifier compatibility API's attempt-prefix environment variable.
[Collection(TestCollections.GoalAcceptanceVerifier)]
public sealed class GoalAcceptanceVerifierTestsGoTestJsonReceipt : GoalAcceptanceVerifierTestBase
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunAsync_GoGate_AttachesReceiptAndDecodesFailures(bool failed)
    {
        var root = CreateManifestWorkspace("""
            {
              "version": 1,
              "checks": [
                { "name": "go build", "type": "command", "command": "go", "arguments": ["build", "./..."] },
                { "name": "go vet", "type": "command", "command": "go", "arguments": ["vet", "./..."] },
                { "name": "go test", "type": "command", "command": "go", "arguments": ["test", "-count=1", "./..."] }
              ]
            }
            """);
        var previousPrefix = Environment.GetEnvironmentVariable(GoalAcceptanceVerifier.AcceptanceAttemptTrxPrefixVariable);
        try
        {
            using var evidence = new ProspectiveEvidenceGateFixture();
            GoTestJsonGateReceiptTests.CreateGoSources(root);
            Directory.CreateDirectory(Path.Combine(root, ".git"));
            var prefix = Path.Combine(root, ".orchestrator", "acceptance-gate-attempts", evidence.Goal.Id.Value, "attempt");
            Environment.SetEnvironmentVariable(GoalAcceptanceVerifier.AcceptanceAttemptTrxPrefixVariable, prefix);
            var invocations = new ConcurrentQueue<string[]>();
            TestOverrides.ResolvePartitionVerdictCandidateTreeShaForTests = _ => "go-candidate-tree";
            TestOverrides.ResolvePartitionVerdictMainShaForTests = _ => "go-main";
            TestOverrides.ResolvePartitionVerdictVerifyingCommitShaForTests = _ => evidence.CandidateSha;
            var verifier = new GoalAcceptanceVerifier(TestOverrides, (arguments, _, _) =>
            {
                invocations.Enqueue(arguments.ToArray());
                var goTest = arguments is ["go", "test", ..];
                return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(
                    goTest && failed ? 1 : 0,
                    goTest ? (failed ? GoTestJsonGateReceiptTests.StreamB : GoTestJsonGateReceiptTests.StreamA) : ""));
            });

            var result = await verifier.RunAsync(root, evidence.Goal.Id);

            var testArguments = Assert.Single(invocations, args => args is ["go", "test", ..]);
            Assert.Equal(new[] { "go", "test", "-json", "-count=1", "./..." }, testArguments);
            Assert.Single(testArguments, argument => argument == "-json");
            foreach (var verb in new[] { "build", "vet" })
            {
                var arguments = Assert.Single(invocations, args => args.Length >= 2 && args[0] == "go" && args[1] == verb);
                Assert.DoesNotContain("-json", arguments);
            }
            Assert.Equal(!failed, result.Passed);
            var check = Assert.Single(result.Checks!, item => item.Name == "go test");
            Assert.Equal(!failed, check.Passed);
            var receipt = Assert.Single(result.TestResultPaths!);
            Assert.Equal(prefix + ".go-test.trx", receipt);
            Assert.True(File.Exists(receipt));
            Assert.Equal(receipt, Assert.Single(check.TestResultPaths!));
            if (failed)
            {
                Assert.Equal(1, check.ExitCode);
                Assert.Contains("--- FAIL: TestStdoutWithJSONOutput", check.OutputTail, StringComparison.Ordinal);
                Assert.DoesNotContain("\"Action\"", check.OutputTail, StringComparison.Ordinal);
            }
            else
            {
                Assert.Null(check.OutputTail);
                var (attemptId, _, _) = evidence.WriteAttempt([], additionalPaths: [receipt]);
                var obligation = GoTestJsonGateReceiptTests.Evaluate(evidence, GoTestJsonGateReceiptTests.LiveSelectors);
                Assert.Equal(GateTestObligationEvaluator.Outcome.Satisfied, obligation.Outcome);
                Assert.Equal(attemptId, obligation.SourceId);
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable(GoalAcceptanceVerifier.AcceptanceAttemptTrxPrefixVariable, previousPrefix);
            DeleteDirectoryWithRetry(root);
        }
    }
}
