using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using System.Text.Json;

// Parallel-safe: repositories and receipt files are isolated in unique temporary roots.
public sealed class WorkerBuildReceiptCoverageTests : WorkerDispatchTestSupport
{
    private static readonly string[] RequiredProjects = ["Parent/Cli/Nested.csproj", "Parent/Parent.csproj"];

    [Xunit.Fact]
    public void MissingProjects_ReturnRequiredSpellingAndOrder()
    {
        Xunit.Assert.Equal(
            ["Parent/Cli/Nested.csproj"],
            WorkerBuildReceiptCoverage.FindMissingProjects(RequiredProjects, ["parent\\PARENT.csproj"]));
        Xunit.Assert.Equal(RequiredProjects, WorkerBuildReceiptCoverage.FindMissingProjects(RequiredProjects, null));
        Xunit.Assert.Equal(RequiredProjects, WorkerBuildReceiptCoverage.FindMissingProjects(RequiredProjects, []));
    }

    [Xunit.Fact]
    public void Coverage_NormalizesBothListsAndAllowsExtraProjects()
    {
        Xunit.Assert.Empty(WorkerBuildReceiptCoverage.FindMissingProjects(
            [@".\Parent\Cli\Nested.csproj", "Parent/Parent.csproj"],
            ["parent/CLI/NESTED.csproj", @".\PARENT\Parent.csproj", "Extra.csproj"]));
        Xunit.Assert.Equal(RequiredProjects, WorkerBuildReceiptCoverage.FindMissingProjects(
            RequiredProjects, ["C:/Elsewhere/Parent/Cli/Nested.csproj", "Other/Parent.csproj"]));
    }

    [Xunit.Theory]
    [Xunit.InlineData("success", false, false)]
    [Xunit.InlineData("failure", true, false)]
    [Xunit.InlineData("unavailable", false, true)]
    [Xunit.InlineData("throws", false, true)]
    public void MatchingDigest_ParentOnlyReceiptRunsNestedBuild(
        string buildOutcome, bool buildFailed, bool missingEvidence)
    {
        var root = CreateSeededDispatchRepository();
        AddProjects(root);
        var receiptPath = WriteReceipt(root, ["Parent/Parent.csproj"]);
        var receipt = WorkerBuildReceipt.Evaluate(receiptPath, root);
        Xunit.Assert.True(receipt.Matches, receipt.Reason);
        Xunit.Assert.Equal(["Parent/Parent.csproj"], receipt.Projects);
        var builds = 0;
        OrchestratorBuildCheckRequest? buildRequest = null;

        var resolution = OrchestratorBuildEvidenceCheck.Resolve(
            root, AgentRole.Developer, ["Parent/Cli/NewTests.cs"], [], false,
            () => WorkerBuildReceipt.Evaluate(receiptPath, root), request =>
            {
                builds++;
                buildRequest = request;
                return buildOutcome switch
                {
                    "success" => new(true, 0, "PASS build: 0 errors"),
                    "failure" => new(true, 1, "FAIL build: 1 error(s)"),
                    "unavailable" => new(false, -1, "script unavailable"),
                    _ => throw new IOException("build launch failed")
                };
            });

        Xunit.Assert.Equal(1, builds);
        Xunit.Assert.NotNull(buildRequest);
        Xunit.Assert.Equal(root, buildRequest.WorktreeRoot);
        Xunit.Assert.Equal(["Parent/Cli/Nested.csproj"], buildRequest.Projects);
        Xunit.Assert.Equal(buildFailed, resolution.BuildFailed);
        Xunit.Assert.Equal(missingEvidence, resolution.MissingEvidence);
        Xunit.Assert.Equal(buildFailed || missingEvidence, resolution.FailsRound);
        Xunit.Assert.Contains(
            "worker_build_receipt=projects-incomplete; missing_projects=Parent/Cli/Nested.csproj;",
            resolution.Diagnostic);
        Xunit.Assert.Contains(missingEvidence ? "build_evidence_attempt=orchestrator" :
            "build_evidence_producer=orchestrator", resolution.Diagnostic);
    }

    [Xunit.Theory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public void MatchingReceipt_AllRequiredProjectsAcceptsCaseAndSeparatorVariants(bool extra)
    {
        var root = CreateSeededDispatchRepository();
        AddProjects(root);
        var listed = new List<string> { @"parent\cli\NESTED.csproj", @".\PARENT\Parent.csproj" };
        if (extra) listed.Add("src/Extra/Extra.csproj");
        var receiptPath = WriteReceipt(root, listed.ToArray());
        var receipt = WorkerBuildReceipt.Evaluate(receiptPath, root);
        Xunit.Assert.True(receipt.Matches, receipt.Reason);
        Xunit.Assert.Equal(listed, receipt.Projects);
        var builds = 0;

        var resolution = OrchestratorBuildEvidenceCheck.Resolve(
            root, AgentRole.Developer, ["Parent/Cli/NewTests.cs", "Parent/Other.cs"], [], false,
            () => WorkerBuildReceipt.Evaluate(receiptPath, root), _ =>
            {
                builds++;
                return new(true, 1, "unexpected build");
            });

        Xunit.Assert.Equal(0, builds);
        Xunit.Assert.False(resolution.FailsRound);
        Xunit.Assert.Equal(string.Empty, resolution.Diagnostic);
    }

    [Xunit.Theory]
    [Xunit.InlineData(true, 0, WorkTaskStatus.Completed)]
    [Xunit.InlineData(true, 1, WorkTaskStatus.Failed)]
    [Xunit.InlineData(false, -1, WorkTaskStatus.Failed)]
    public void PartialReceipt_DispatchUsesInjectedRunnerVerdict(
        bool ran, int buildExitCode, WorkTaskStatus expectedStatus)
    {
        var root = CreateSeededDispatchRepository();
        var clock = new TestClock(DateTimeOffset.Parse("2026-10-09T22:00:00Z"));
        string? receiptPath = null;
        var (kernel, goal, task, process) = CreateCompletedGoalWorktreeDispatch(
            root, AgentRole.Developer,
            WorkerResultBlock("Parent/Cli/NewTests.cs, Parent/Other.cs", "implemented tests",
                "deferred - acceptance gate owns tests"),
            string.Empty, clock, worktree =>
            {
                AddProjects(worktree);
                receiptPath = WriteReceipt(worktree, ["Parent/Parent.csproj"]);
            });
        var receipt = WorkerBuildReceipt.Evaluate(receiptPath!, process.WorkingDirectory);
        Xunit.Assert.True(receipt.Matches, receipt.Reason);
        Xunit.Assert.Equal(["Parent/Parent.csproj"], receipt.Projects);
        var builds = 0;
        const string injectedOutput = "injected build runner verdict; no script launched";
        var runner = new BackgroundDispatchRunner(clock,
            runOrchestratorBuildCheck: request =>
            {
                builds++;
                Xunit.Assert.Equal(process.WorkingDirectory, request.WorktreeRoot);
                Xunit.Assert.Equal(RequiredProjects, request.Projects);
                return new(ran, buildExitCode, injectedOutput);
            },
            resolveWorkerBuildReceiptPath: _ => receiptPath!);

        runner.RefreshLatestProcess(kernel, goal.Id, task.Id);

        Xunit.Assert.Equal(1, builds);
        Xunit.Assert.Equal(expectedStatus, task.Status);
        var verification = Xunit.Assert.IsType<TaskVerificationRecord>(task.LastVerification);
        Xunit.Assert.Equal(expectedStatus == WorkTaskStatus.Completed ? 0 : 1, verification.ExitCode);
        Xunit.Assert.Contains(
            "worker_build_receipt=projects-incomplete; missing_projects=Parent/Cli/Nested.csproj;",
            verification.StandardError);
        Xunit.Assert.Contains(injectedOutput, verification.StandardError);
        var expectedMarker = ran
            ? "build_evidence_producer=orchestrator"
            : "build_evidence_attempt=orchestrator";
        Xunit.Assert.Contains(expectedMarker, verification.StandardError);
        if (expectedStatus == WorkTaskStatus.Completed)
            Xunit.Assert.Equal(string.Empty, ReadGit(process.WorkingDirectory, ["status", "--short"]));
    }

    [Xunit.Fact]
    public void MatchingVerdict_WithoutProjectsBuildsAllRequiredProjects()
    {
        var root = CreateTempDirectory();
        AddProjects(root);
        var builds = 0;
        var resolution = OrchestratorBuildEvidenceCheck.Resolve(
            root, AgentRole.Developer, ["Parent/Cli/NewTests.cs"], ["Parent/Other.cs"], false,
            () => new(true, "matched"), request =>
            {
                builds++;
                Xunit.Assert.Equal(RequiredProjects, request.Projects);
                return new(true, 0, "PASS build: 0 errors");
            });

        Xunit.Assert.Equal(1, builds);
        Xunit.Assert.False(resolution.FailsRound);
        Xunit.Assert.Contains(
            "missing_projects=Parent/Cli/Nested.csproj, Parent/Parent.csproj;", resolution.Diagnostic);
    }

    [Xunit.Theory]
    [Xunit.InlineData("Parent/readme.md")]
    [Xunit.InlineData("Parent/Excluded.cs")]
    public void NoCompiledChange_SkipsReceiptAndBuild(string changedPath)
    {
        var root = CreateTempDirectory();
        AddProjects(root);
        var receipts = 0;
        var builds = 0;
        var resolution = OrchestratorBuildEvidenceCheck.Resolve(
            root, AgentRole.Developer, [changedPath], [], false,
            () => { receipts++; return new(false, "receipt-missing"); },
            _ => { builds++; return new(true, 1, "unexpected build"); });

        Xunit.Assert.Equal(0, receipts);
        Xunit.Assert.Equal(0, builds);
        Xunit.Assert.False(resolution.FailsRound);
        Xunit.Assert.Equal(string.Empty, resolution.Diagnostic);
    }

    private static void AddProjects(string root)
    {
        var parent = Path.Combine(root, "Parent");
        var cli = Path.Combine(parent, "Cli");
        Directory.CreateDirectory(cli);
        File.WriteAllText(Path.Combine(parent, "Parent.csproj"), """
            <Project><ItemGroup><Compile Remove="Cli\**\*.cs;Excluded.cs" /></ItemGroup></Project>
            """);
        File.WriteAllText(Path.Combine(cli, "Nested.csproj"), "<Project />");
        File.WriteAllText(Path.Combine(cli, "NewTests.cs"), "public class NewTests { }");
        File.WriteAllText(Path.Combine(parent, "Other.cs"), "public class Other { }");
        File.WriteAllText(Path.Combine(parent, "Excluded.cs"), "public class Excluded { }");
    }

    private static string WriteReceipt(string root, string[] projects)
    {
        Xunit.Assert.True(WorktreeTreeDigest.TryCompute(root, out var digest, out var failure), failure);
        var path = Path.Combine(CreateTempDirectory(), WorkerBuildReceipt.FileName);
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            treeDigest = digest,
            digestAlgorithm = WorktreeTreeDigest.Algorithm,
            buildOutcome = "success",
            generatedAtUtc = "2026-10-09T22:00:00Z",
            worktreeRoot = root,
            configuration = "Debug",
            projects
        }));
        return path;
    }
}
