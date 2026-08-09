using System.Text.Json;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record PostLandingCanaryProbeResult(
    bool Green,
    PostLandingCanaryFailureReason? FailureReason,
    int ExecutedTestCount,
    string Detail);

internal static class PostLandingCanaryCommand
{
    internal const string SubcommandName = "__post-landing-canary";
    internal const string ResultPrefix = "CANARY_RESULT ";

    internal static int Run(
        IReadOnlyList<string> args,
        IGoalAcceptanceVerifier? acceptanceVerifier = null,
        Func<IReadOnlyList<string>, int>? completedTestCounter = null)
    {
        if (args.Count != 2 || !args[0].Equals(SubcommandName, StringComparison.Ordinal))
        {
            Console.Error.WriteLine($"Usage: {SubcommandName} <fixture-root>");
            return 2;
        }

        PostLandingCanaryProbeResult probe;
        try
        {
            var verification = (acceptanceVerifier ?? new GoalAcceptanceVerifier())
                .RunAsync(
                    args[1],
                    goalId: null,
                    changedFiles: ["tests/Mcg.AgentOrchestrator.Core.Tests/CanaryTests.cs"])
                .GetAwaiter()
                .GetResult();
            var resultPaths = verification.TestResultPaths ?? [];
            var executedTestCount = completedTestCounter is null
                ? TestCoverageInvariant.ReadCompletedTests(resultPaths).Count
                : completedTestCounter(resultPaths);
            probe = verification.Passed && executedTestCount > 0
                ? new PostLandingCanaryProbeResult(
                    true,
                    null,
                    executedTestCount,
                    $"accept verdict with {executedTestCount} executed test(s)")
                : verification.Passed
                    ? new PostLandingCanaryProbeResult(
                        false,
                        PostLandingCanaryFailureReason.EmptyReceipt,
                        0,
                        "accept verdict had an empty or missing core-tests receipt")
                    : new PostLandingCanaryProbeResult(
                        false,
                        ClassifyFailure(verification, executedTestCount),
                        executedTestCount,
                        verification.OutputTail ?? "gate returned a reject verdict");
        }
        catch (Exception ex)
        {
            probe = new PostLandingCanaryProbeResult(
                false,
                PostLandingCanaryFailureReason.InfrastructureError,
                0,
                $"{ex.GetType().Name}: {ex.Message}");
        }

        Console.WriteLine(ResultPrefix + JsonSerializer.Serialize(
            probe,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        Console.Out.Flush();
        return probe.Green ? 0 : 1;
    }

    internal static PostLandingCanaryFailureReason ClassifyFailure(
        AcceptanceVerificationResult verification,
        int executedTestCount)
    {
        // Missing gate checks or explicit environment interference are canary-infrastructure failures.
        // A gate rejection with checks remains a verdict failure even if its reporter did not flush a TRX.
        if (verification.Checks is null or { Count: 0 } ||
            verification.Checks.Any(check =>
                string.Equals(
                    check.FailureClassification,
                    AcceptanceFailureClassifications.GateEnvironmentInterference,
                    StringComparison.Ordinal)))
        {
            return PostLandingCanaryFailureReason.InfrastructureError;
        }

        return PostLandingCanaryFailureReason.Reject;
    }
}
