using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.Infrastructure.Tests;

public sealed class GoalAcceptanceEvidenceBundleTests
{
    private const int MaxGitStreamDiagnosticLength = 256;

    [Xunit.Fact]
    public void ChangedFiles_FailedDiff_ThrowsInsteadOfReturningKnownEmpty()
    {
        var root = Path.Combine(Path.GetTempPath(), "mcg-changed-files-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Xunit.Assert.Throws<InvalidOperationException>(() =>
                GoalAcceptanceEvidenceBundleBuilder.GetChangedFiles(root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Fact]
    public void ChangedFiles_FailedBaseRefProbe_ThrowsInsteadOfReturningKnownEmpty()
    {
        var root = Path.Combine(Path.GetTempPath(), "mcg-changed-files-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            RunGit(root, "init", "-b", "topic");
            RunGit(root, "config", "user.email", "tests@example.invalid");
            RunGit(root, "config", "user.name", "Tests");
            File.WriteAllText(Path.Combine(root, "seed.txt"), "seed");
            RunGit(root, "add", "seed.txt");
            RunGit(root, "commit", "-m", "Seed");

            var corruptMainRef = Path.Combine(root, ".git", "refs", "heads", "main");
            Directory.CreateDirectory(Path.GetDirectoryName(corruptMainRef)!);
            File.WriteAllText(corruptMainRef, "not-an-object-id");

            var exception = Xunit.Assert.Throws<InvalidOperationException>(() =>
                GoalAcceptanceEvidenceBundleBuilder.GetChangedFiles(root));

            Xunit.Assert.Contains("git base-ref discovery failed", exception.Message, StringComparison.Ordinal);
            Xunit.Assert.Contains("ref=main", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(path, FileAttributes.Normal);
            }

            Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Theory]
    [Xunit.InlineData(false, 0, false, "process-started=false")]
    [Xunit.InlineData(true, 7, false, "exit-code=7")]
    [Xunit.InlineData(true, 0, true, "drain-timed-out=true")]
    public void ChangedFiles_UnsuccessfulDiffState_ThrowsBoundedDiagnostic(
        bool processStarted,
        int exitCode,
        bool drainTimedOut,
        string expectedDiagnostic)
    {
        var result = new GitCli.GitResult(
            exitCode,
            string.Empty,
            new string('x', 2_000),
            drainTimedOut,
            processStarted);

        var exception = Xunit.Assert.Throws<InvalidOperationException>(() =>
            GoalAcceptanceEvidenceBundleBuilder.ParseChangedFilesResult(result));

        Xunit.Assert.Contains(expectedDiagnostic, exception.Message, StringComparison.Ordinal);
        Xunit.Assert.Contains("...(truncated)", exception.Message, StringComparison.Ordinal);
        Xunit.Assert.True(exception.Message.Length < 1_000, exception.Message);
    }

    [Xunit.Fact]
    public void ChangedFiles_SuccessfulEmptyDiff_ReturnsKnownEmpty()
    {
        var files = GoalAcceptanceEvidenceBundleBuilder.ParseChangedFilesResult(
            new GitCli.GitResult(0, string.Empty, string.Empty));

        Xunit.Assert.Empty(files);
    }

    [Xunit.Fact]
    public void RunGitResult_ExitZeroDrainTimeout_IsAccepted()
    {
        var result = new GitCli.GitResult(
            0,
            "setup output",
            "advisory error output",
            DrainTimedOut: true);

        AssertGitSucceeded("init -b topic", result);
    }

    [Xunit.Fact]
    public void RunGitResult_NonzeroExit_ReportsStateAndStreams()
    {
        var result = new GitCli.GitResult(
            7,
            "stdout detail" + new string('o', 1_000),
            "stderr detail" + new string('e', 1_000),
            DrainTimedOut: true);

        var exception = Xunit.Assert.Throws<Xunit.Sdk.TrueException>(() =>
            AssertGitSucceeded("config user.name Tests", result));

        Xunit.Assert.Contains("ExitCode=7", exception.Message, StringComparison.Ordinal);
        Xunit.Assert.Contains("ProcessStarted=true", exception.Message, StringComparison.Ordinal);
        Xunit.Assert.Contains("DrainTimedOut=true", exception.Message, StringComparison.Ordinal);
        Xunit.Assert.Contains("Output=stdout detail", exception.Message, StringComparison.Ordinal);
        Xunit.Assert.Contains("Error=stderr detail", exception.Message, StringComparison.Ordinal);
        Xunit.Assert.Contains("...(truncated)", exception.Message, StringComparison.Ordinal);
        Xunit.Assert.True(exception.Message.Length < 1_000, exception.Message);
    }

    [Xunit.Fact]
    public void RunGitResult_StartFailure_LabelsEmptyStreams()
    {
        var result = new GitCli.GitResult(
            1,
            string.Empty,
            string.Empty,
            ProcessStarted: false);

        var exception = Xunit.Assert.Throws<Xunit.Sdk.TrueException>(() =>
            AssertGitSucceeded("init -b topic", result));

        Xunit.Assert.Contains("ProcessStarted=false", exception.Message, StringComparison.Ordinal);
        Xunit.Assert.Contains("Output=<empty>; Error=<empty>", exception.Message, StringComparison.Ordinal);
    }

    [Xunit.Theory(DisplayName = "Acceptance_policy_recognizes_equivalent_aggregate_project_test_evidence")]
    [Xunit.InlineData(
        "full dotnet tests: infrastructure",
        "dotnet test --project tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj --verbosity minimal",
        "infrastructure tests")]
    [Xunit.InlineData(
        "full dotnet tests: core",
        "dotnet test --project tests/Mcg.AgentOrchestrator.Core.Tests/Mcg.AgentOrchestrator.Core.Tests.csproj --verbosity minimal",
        "core tests")]
    [Xunit.InlineData(
        "full dotnet tests: provider environment",
        "dotnet test --project tests/Mcg.AgentOrchestrator.Infrastructure.Tests/ProviderEnvironment/Mcg.AgentOrchestrator.Infrastructure.ProviderEnvironment.Tests.csproj --verbosity minimal",
        "provider environment tests")]
    [Xunit.InlineData(
        "full dotnet tests: cli",
        "dotnet test --project tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Cli/Mcg.AgentOrchestrator.Infrastructure.Cli.Tests.csproj --verbosity minimal",
        "cli tests")]
    [Xunit.InlineData(
        "full dotnet tests: dashboard",
        "dotnet test tests/Mcg.AgentOrchestrator.Dashboard.Tests/Mcg.AgentOrchestrator.Dashboard.Tests.csproj --verbosity minimal",
        "dashboard tests")]
    public void AcceptancePolicyRecognizesEquivalentAggregateProjectTestEvidence(
        string policyName,
        string commandLine,
        string aggregateName)
    {
        var policyCheck = new VerificationPolicyCheck(
            policyName,
            "dotnet-test",
            Required: true,
            commandLine,
            "Broad verification required.");

        var passed = GoalAcceptanceEvidenceBundleBuilder.ResolvePolicyCheckState(
            policyCheck,
            [new AcceptanceCheckResult(aggregateName, true, 0, null)]);
        var failed = GoalAcceptanceEvidenceBundleBuilder.ResolvePolicyCheckState(
            policyCheck,
            [new AcceptanceCheckResult(aggregateName, false, 1, "failed")]);

        Xunit.Assert.Equal("passed", passed);
        Xunit.Assert.Equal("failed", failed);
    }

    [Xunit.Fact(DisplayName = "Acceptance_policy_recognizes_solution_test_evidence_only_when_all_projects_pass")]
    public void AcceptancePolicyRecognizesSolutionTestEvidenceOnlyWhenAllProjectsPass()
    {
        var policyCheck = SolutionPolicyCheck();
        AcceptanceCheckResult[] allPassed =
        [
            new("core tests", true, 0, null),
            new("infrastructure tests", true, 0, null),
            new("provider environment tests", true, 0, null),
            new("cli tests", true, 0, null)
        ];

        var passed = GoalAcceptanceEvidenceBundleBuilder.ResolvePolicyCheckState(policyCheck, allPassed);
        var failed = GoalAcceptanceEvidenceBundleBuilder.ResolvePolicyCheckState(
            policyCheck,
            [.. allPassed[..3], new AcceptanceCheckResult("cli tests", false, 1, "failed")]);
        var missing = GoalAcceptanceEvidenceBundleBuilder.ResolvePolicyCheckState(
            policyCheck,
            allPassed[..3]);

        Xunit.Assert.Equal("passed", passed);
        Xunit.Assert.Equal("failed", failed);
        Xunit.Assert.Equal("missing", missing);
    }

    [Xunit.Fact(DisplayName = "Acceptance_policy_does_not_treat_partition_check_as_full_aggregate")]
    public void AcceptancePolicyDoesNotTreatPartitionCheckAsFullAggregate()
    {
        var policyCheck = new VerificationPolicyCheck(
            "full dotnet tests: infrastructure",
            "dotnet-test",
            Required: true,
            "dotnet test --project tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj --verbosity minimal",
            "Broad verification required.");

        var state = GoalAcceptanceEvidenceBundleBuilder.ResolvePolicyCheckState(
            policyCheck,
            [new AcceptanceCheckResult("infrastructure tests: Cli", true, 0, null)]);

        Xunit.Assert.Equal("missing", state);
    }

    [Xunit.Fact(DisplayName = "Acceptance_policy_does_not_treat_advisory_check_as_full_aggregate")]
    public void AcceptancePolicyDoesNotTreatAdvisoryCheckAsFullAggregate()
    {
        var policyCheck = new VerificationPolicyCheck(
            "full dotnet tests: infrastructure",
            "dotnet-test",
            Required: true,
            "dotnet test --project tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj --verbosity minimal",
            "Broad verification required.");

        var state = GoalAcceptanceEvidenceBundleBuilder.ResolvePolicyCheckState(
            policyCheck,
            [new AcceptanceCheckResult("infrastructure tests", true, 0, null, Advisory: true)]);

        Xunit.Assert.Equal("missing", state);
    }

    private static VerificationPolicyCheck SolutionPolicyCheck() => new(
        "full dotnet tests",
        "dotnet-test",
        Required: true,
        "dotnet test Mcg.AgentOrchestrator.sln --verbosity minimal",
        "Broad verification required.");

    private static void RunGit(string workingDirectory, params string[] args)
    {
        var result = GitCli.Run(workingDirectory, args);
        AssertGitSucceeded(string.Join(' ', args), result);
    }

    private static void AssertGitSucceeded(string command, GitCli.GitResult result)
    {
        Xunit.Assert.True(
            result.Succeeded,
            $"git {command} failed. ExitCode={result.ExitCode}; " +
            $"ProcessStarted={result.ProcessStarted.ToString().ToLowerInvariant()}; " +
            $"DrainTimedOut={result.DrainTimedOut.ToString().ToLowerInvariant()}; " +
            $"Output={FormatGitStream(result.Output)}; Error={FormatGitStream(result.Error)}");
    }

    private static string FormatGitStream(string value) => string.IsNullOrEmpty(value)
        ? "<empty>"
        : value.Length <= MaxGitStreamDiagnosticLength
            ? value
            : $"{value[..MaxGitStreamDiagnosticLength]}...(truncated)";
}
