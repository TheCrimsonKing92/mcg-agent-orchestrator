using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using static ConductorDriverTests;
using static CheckDeclaredFocusedEvidenceProjectTests;
using AcceptanceManifestCheck = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.AcceptanceManifestCheck;

public sealed class CheckDeclaredFocusedEvidenceConductorTests
{
    [Fact]
    public void FindingRequest_CheckDeclaredVstestProject_HonoursFocusedCheck()
    {
        using var fixture = new Fixture();
        var settings = fixture.LoadSettings();
        var (kernel, goal) = SoftwareGoal();
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        foreach (var task in goal.Tasks.Where(task => task.RequiredRole != AgentRole.Reviewer))
        {
            PassVerification(kernel, goal, task);
        }

        var finding = EvidenceFindingWithRequest(
            "Check-declared VSTest project needs focused evidence.",
            id: "net-health-check-project", project: "NetHealth.App.Tests", classes: ["HealthProbeTests"]);
        FailReviewerNeedsWork(kernel, goal, reviewer, "check-declared request", findings: [finding]);
        string? capturedRequest = null;
        AcceptanceManifestCheck? built = null;
        var driver = MakeDriver(
            getPreReviewEvidenceContext: _ => NoPreReviewContext("abc1234"),
            getFindingEvidenceEngineSettings: _ => settings,
            runFocusedEvidence: (_, request) =>
            {
                capturedRequest = request;
                built = fixture.Build(request, fixture.LoadSettings());
                return new FocusedEvidenceRunResult(request, true, true, "VSTest focused check passed", []);
            },
            retryTaskWithRoundKind: (goalId, taskId, message, roundKind) =>
                kernel.RetryTask(goalId, taskId, message, retryRoundKind: roundKind),
            recordFindingEvidenceOutcome: (goalId, taskId, stableId, outcome, receipt) =>
                kernel.RecordFindingEvidenceOutcome(goalId, taskId, stableId, outcome, receipt));

        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.Equal(AppProject + ":HealthProbeTests", capturedRequest);
        var outcome = reviewer.VerificationHistory.Last().MergedReviewFindings!
            .Single(item => item.StableId == "net-health-check-project").EvidenceOutcome;
        Assert.True(outcome?.Honoured);
        Assert.NotEqual(FindingEvidenceNotHonouredReason.UnsupportedProject, outcome?.Reason);
        Assert.NotNull(built);
        Assert.Equal(AppProject, built.Project);
        Assert.Equal("vstest", built.Runner);
        Assert.False(AcceptanceCheckCommandBuilder.UsesMicrosoftTestingPlatform(built));
        Assert.Equal(17, built.TimeoutMinutes);
        Assert.Equal(["--configuration", "Release", "--filter",
            "(Category!=Slow)&(FullyQualifiedName~HealthProbeTests)"], built.Arguments);
        var command = AcceptanceCheckCommandBuilder.BuildDotnetTestArguments(built);
        Assert.Equal(["dotnet", "test", AppProject], command.Take(3));
        Assert.Equal(1, command.Count(argument => argument == "--filter"));
        Assert.Equal("(Category!=Slow)&(FullyQualifiedName~HealthProbeTests)",
            command[Array.IndexOf(command, "--filter") + 1]);
    }

    [Theory]
    [InlineData("vstest")]
    [InlineData("unknown")]
    [InlineData(null)]
    public void Build_CheckOnlyProject_UsesVstestAndPreservesDefaultTimeout(string? runner)
    {
        using var fixture = new Fixture();
        var settings = AcceptanceGateEngineSettings.Parse(JsonSerializer.Serialize(new
        {
            checks = new[] { new { type = "dotnet-test", project = CoreProject, runner,
                arguments = new[] { "--verbosity", "quiet" } } }
        }));

        var check = fixture.Build("NetHealth.Core.Tests:AlphaTests; NetHealth.Core.Tests:BetaTests", settings);

        Assert.Equal(CoreProject, check.Project);
        Assert.Equal("vstest", check.Runner);
        Assert.Null(check.TimeoutMinutes);
        Assert.Equal(["--verbosity", "quiet", "--filter",
            "FullyQualifiedName~AlphaTests|FullyQualifiedName~BetaTests"], check.Arguments);
        Assert.Equal(2, check.FocusedEvidenceTokens.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Build_ExistingManifestFilter_AndJoinsClassFilterOnce(bool inlineFilter)
    {
        using var fixture = new Fixture();
        var arguments = inlineFilter
            ? new[] { "--no-restore", "--filter=Category!=Slow|Category=Fast" }
            : new[] { "--no-restore", "--filter", "Category!=Slow|Category=Fast" };
        var settings = AcceptanceGateEngineSettings.Parse(JsonSerializer.Serialize(new
        {
            checks = new[] { new { type = "dotnet-test", project = AppProject, arguments } }
        }));

        var check = fixture.Build("NetHealth.App.Tests:HealthProbeTests", settings);

        Assert.Equal(["--no-restore", "--filter",
            "(Category!=Slow|Category=Fast)&(FullyQualifiedName~HealthProbeTests)"], check.Arguments);
    }

    [Fact]
    public void Build_MappedProject_PreservesDeclaredArgumentsAndTimeout()
    {
        using var fixture = new Fixture();
        var settings = fixture.LoadSettings();

        var check = fixture.Build("NetHealth.App.Tests:mapped-project", settings);

        Assert.Equal(settings.DeclaredDotnetTestChecks[1].Arguments, check.Arguments);
        Assert.Equal(17, check.TimeoutMinutes);
        Assert.Equal("vstest", check.Runner);
        Assert.False(check.IsFocusedEvidenceSelection);
        Assert.Empty(check.FocusedEvidenceTokens);
    }

    [Fact]
    public void Build_OverlappingMtpDeclaration_KeepsExistingFocusedShape()
    {
        using var fixture = new Fixture();
        const string engine = """
            "engine": { "mtpInvocations": [
              { "project": "tests/NetHealth.App.Tests/NetHealth.App.Tests.csproj",
                "executablePathTemplate": "bin/{projectName}{executableExtension}",
                "arguments": ["{executable}", "--no-ansi"] }
            ] }
            """;
        var before = AcceptanceGateEngineSettings.Parse("{" + engine + "}");
        var after = AcceptanceGateEngineSettings.Parse(Manifest.Insert(1, engine + ","));
        const string request = "NetHealth.App.Tests:HealthProbeTests";

        var original = fixture.Build(request, before);
        var overlapping = fixture.Build(request, after);

        Assert.Equal("mtp", overlapping.Runner);
        Assert.Equal(original.Runner, overlapping.Runner);
        Assert.Equal(original.Project, overlapping.Project);
        Assert.Equal(original.Name, overlapping.Name);
        Assert.Equal(original.Arguments, overlapping.Arguments);
        Assert.Equal(original.TimeoutMinutes, overlapping.TimeoutMinutes);
        Assert.Equal(original.FocusedEvidenceTokens, overlapping.FocusedEvidenceTokens);
        Assert.Equal(GoalAcceptanceVerifier.FocusedEvidenceSupportedProjectForms(before),
            DeclaredTestProjectInventory.DescribeSupportedProjectForms(after));
    }

    [Fact]
    public void Arguments_MissingDeclaredFilterValue_FailsLoudly()
    {
        Assert.Throws<InvalidDataException>(() => VstestFocusedEvidenceCheckShape.Arguments(
            new AcceptanceManifestCheck { Arguments = ["--filter"] }, "FullyQualifiedName~HealthProbeTests"));
    }

    private sealed class Fixture : IDisposable
    {
        private string Root { get; } = InfrastructureTestSupport.CreateTempDirectory();

        internal Fixture()
        {
            Directory.CreateDirectory(Path.Combine(Root, "config"));
            File.WriteAllText(Path.Combine(Root, "config", "acceptance-manifest.json"), Manifest);
            foreach (var project in new[] { CoreProject, AppProject })
            {
                var projectPath = Path.Combine(Root, project);
                Directory.CreateDirectory(Path.GetDirectoryName(projectPath)!);
                File.WriteAllText(projectPath, "<Project Sdk=\"Microsoft.NET.Sdk\" />");
                File.WriteAllText(Path.Combine(Path.GetDirectoryName(projectPath)!, "HealthProbeTests.cs"),
                    "public class HealthProbeTests { } public class AlphaTests { } public class BetaTests { }");
            }
        }

        internal AcceptanceGateEngineSettings LoadSettings() => AcceptanceGateEngineSettings.Load(Root);

        internal AcceptanceManifestCheck Build(string request, AcceptanceGateEngineSettings settings)
        {
            Assert.True(GoalAcceptanceVerifier.TryBuildFocusedEvidenceChecks(
                request, settings, Root, out var checks, out _, out var rejection), rejection.Detail);
            return Assert.Single(checks);
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
