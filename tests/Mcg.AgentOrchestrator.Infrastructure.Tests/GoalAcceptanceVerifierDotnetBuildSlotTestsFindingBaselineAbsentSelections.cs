using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection(TestCollections.JobAccounting)]
public sealed class GoalAcceptanceVerifierDotnetBuildSlotTestsFindingBaselineAbsentSelections
    : GoalAcceptanceVerifierDotnetBuildSlotTests
{
    [Xunit.Fact]
    public async Task ExistingSelectionRunsWhileAddedClassIsAbsentAtBaseline()
    {
        using var fixture = FindingBaselineProbeFixture.Create();
        var result = await fixture.RunAsync();
        var baseline = Assert.Single(result.Arms!, arm => arm.Arm == FindingEvidenceArm.Baseline);

        Assert.Equal(FindingEvidenceArmDisposition.Green, baseline.Disposition);
        Assert.Contains(baseline.Checks, check => check.ExecutedTestCount > 0 &&
            check.Name.Contains("PreExistingProbeTests", StringComparison.Ordinal));
        Assert.Contains(baseline.Checks, check =>
            check.Name.Contains("AddedProbeTests", StringComparison.Ordinal) &&
            check.FailureClassification == AcceptanceFailureClassifications.FocusedSelectionAbsentAtBaseline);
        Assert.Equal(FindingEvidenceOutcomeReason.CandidateRed, result.OutcomeReason);
    }

    [Xunit.Fact]
    public async Task ExistingClassWithNoBaselineTestsRemainsApparatusFailure()
    {
        using var fixture = FindingBaselineProbeFixture.Create(zeroBaselineTests: true);
        var result = await fixture.RunAsync();
        var baseline = Assert.Single(result.Arms!, arm => arm.Arm == FindingEvidenceArm.Baseline);

        Assert.Equal(FindingEvidenceArmDisposition.ApparatusFailure, baseline.Disposition);
        Assert.Contains(baseline.Checks, check =>
            check.FailureClassification == AcceptanceFailureClassifications.FocusedSelectionApparatusFailure &&
            check.ExecutedTestCount == 0);
        Assert.DoesNotContain(baseline.Checks, check =>
            check.Name.Contains("PreExistingProbeTests", StringComparison.Ordinal) &&
            check.FailureClassification == AcceptanceFailureClassifications.FocusedSelectionAbsentAtBaseline);
    }

    [Xunit.Fact]
    public async Task AllCandidateOnlySelectionsHaveUsableTypedBaseline()
    {
        using var fixture = FindingBaselineProbeFixture.Create();
        var result = await fixture.RunAsync("Core.Tests: AddedProbeTests");
        var baseline = Assert.Single(result.Arms!, arm => arm.Arm == FindingEvidenceArm.Baseline);

        Assert.Equal(FindingEvidenceArmDisposition.Green, baseline.Disposition);
        Assert.All(baseline.Checks, check => Assert.Equal(
            AcceptanceFailureClassifications.FocusedSelectionAbsentAtBaseline,
            check.FailureClassification));
    }

    [Xunit.Fact]
    public async Task CandidateAddedMethodOnExistingClassIsAbsentAtBaseline()
    {
        using var fixture = FindingBaselineProbeFixture.Create(candidateAddedMethod: true);
        var result = await fixture.RunAsync(
            "Core.Tests: PreExistingProbeTests.AddedBehavior; Core.Tests: PreExistingProbeTests.ExistingBehavior");
        var baseline = Assert.Single(result.Arms!, arm => arm.Arm == FindingEvidenceArm.Baseline);

        Assert.Equal(FindingEvidenceArmDisposition.Green, baseline.Disposition);
        Assert.Contains(baseline.Checks, check => check.ExecutedTestCount > 0 &&
            check.Name.Contains("ExistingBehavior", StringComparison.Ordinal));
        Assert.Contains(baseline.Checks, check =>
            check.Name.Contains("AddedBehavior", StringComparison.Ordinal) &&
            check.FailureClassification == AcceptanceFailureClassifications.FocusedSelectionAbsentAtBaseline);
    }
}

internal sealed class FindingBaselineProbeFixture : IDisposable
{
    private const string TestProject = "tests/Mcg.AgentOrchestrator.Core.Tests";
    private readonly GoalId _goalId = GoalId.New();

    private FindingBaselineProbeFixture(string root)
    {
        Root = root;
    }

    internal string Root { get; }
    internal string AddedTestPath => $"{TestProject}/AddedProbeTests.cs";
    internal string FeaturePath => $"{TestProject}/Feature.cs";
    internal string ExistingTestPath => $"{TestProject}/PreExistingProbeTests.cs";

    internal static FindingBaselineProbeFixture Create(
        bool zeroBaselineTests = false, bool candidateAddedMethod = false)
    {
        var root = Path.Combine(Path.GetTempPath(), "mcg-finding-baseline", Guid.NewGuid().ToString("N"));
        var project = Path.Combine(root, "tests", "Mcg.AgentOrchestrator.Core.Tests");
        Directory.CreateDirectory(Path.Combine(root, "config"));
        Directory.CreateDirectory(project);
        File.WriteAllText(Path.Combine(root, "config", "acceptance-manifest.json"), """
            {
              "version": 1,
              "engine": {
                "slotCount": 1,
                "maxConcurrentShards": 1,
                "enforceStructuralCoverage": false,
                "mtpInvocations": [{
                  "project": "tests/Mcg.AgentOrchestrator.Core.Tests/Mcg.AgentOrchestrator.Core.Tests.csproj",
                  "executablePathTemplate": "bin/{projectName}/{configuration}/{projectName}{executableExtension}",
                  "arguments": ["{executable}", "--results-directory", "{resultsDirectory}", "--report-trx", "--report-trx-filename", "{trxFileName}"]
                }]
              },
              "checks": [],
              "forbiddenChangedPathGlobs": []
            }
            """);
        File.WriteAllText(Path.Combine(root, "Directory.Build.props"), """
            <Project><PropertyGroup><UseSharedCompilation>false</UseSharedCompilation><RestoreIgnoreFailedSources>true</RestoreIgnoreFailedSources><NuGetAudit>false</NuGetAudit></PropertyGroup></Project>
            """);
        File.WriteAllText(Path.Combine(root, "NuGet.Config"), """
            <?xml version="1.0" encoding="utf-8"?><configuration><packageSources><clear /></packageSources></configuration>
            """);
        File.WriteAllText(Path.Combine(project, "Mcg.AgentOrchestrator.Core.Tests.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable><IsTestProject>true</IsTestProject><OutputType>Exe</OutputType><UseMicrosoftTestingPlatformRunner>true</UseMicrosoftTestingPlatformRunner></PropertyGroup><ItemGroup><PackageReference Include="Microsoft.Testing.Extensions.TrxReport" Version="2.3.2" /><PackageReference Include="xunit.v3.mtp-v2" Version="3.2.2" /></ItemGroup></Project>
            """);
        File.WriteAllText(Path.Combine(project, "Feature.cs"),
            "public static class Feature { public static bool Enabled => false; }");
        File.WriteAllText(Path.Combine(project, "PreExistingProbeTests.cs"), zeroBaselineTests
            ? "public sealed class PreExistingProbeTests { }"
            : "public sealed class PreExistingProbeTests { [Xunit.Fact] public void ExistingBehavior() => Xunit.Assert.False(Feature.Enabled); }");
        if (zeroBaselineTests)
        {
            File.WriteAllText(Path.Combine(project, "OutsideProbeTests.cs"),
                "public sealed class OutsideProbeTests { [Xunit.Fact] public void OutsideBehavior() => Xunit.Assert.False(Feature.Enabled); }");
        }
        Git(root, "init", "-b", "main");
        Git(root, "config", "user.email", "finding-baseline@example.invalid");
        Git(root, "config", "user.name", "Finding Baseline Fixture");
        Git(root, "add", ".");
        Git(root, "commit", "-m", "baseline");
        Git(root, "checkout", "-b", "goal/finding-baseline");
        File.WriteAllText(Path.Combine(project, "Feature.cs"),
            "public static class Feature { public static bool Enabled => true; }");
        if (zeroBaselineTests)
        {
            File.WriteAllText(Path.Combine(project, "PreExistingProbeTests.cs"),
                "public sealed class PreExistingProbeTests { [Xunit.Fact] public void ExistingBehavior() => Xunit.Assert.False(Feature.Enabled); }");
        }
        else if (candidateAddedMethod)
        {
            File.WriteAllText(Path.Combine(project, "PreExistingProbeTests.cs"),
                "public sealed class PreExistingProbeTests { [Xunit.Fact] public void ExistingBehavior() => Xunit.Assert.False(Feature.Enabled); [Xunit.Fact] public void AddedBehavior() => Xunit.Assert.False(Feature.Enabled); }");
        }
        File.WriteAllText(Path.Combine(project, "AddedProbeTests.cs"),
            "public sealed class AddedProbeTests { [Xunit.Fact] public void AddedBehavior() => Xunit.Assert.False(Feature.Enabled); }");
        Git(root, "add", ".");
        Git(root, "commit", "-m", "candidate");
        return new FindingBaselineProbeFixture(root);
    }

    internal Task<FocusedEvidenceRunResult> RunAsync(string request =
        "Core.Tests: AddedProbeTests; Core.Tests: PreExistingProbeTests") =>
        new GoalAcceptanceVerifier().RunFocusedEvidenceAsync(Root, _goalId, request, runBaselineArm: true);

    public void Dispose()
    {
        DotnetBuildEnvironmentManager.TryDeleteGoalArtifacts(_goalId);
        for (var attempt = 0; attempt < 10 && Directory.Exists(Root); attempt++)
        {
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch (IOException) when (attempt < 9)
            {
                Thread.Sleep(100);
            }
        }
    }

    private static void Git(string root, params string[] arguments)
    {
        var result = GitCli.Run(root, arguments);
        Assert.True(result.Succeeded, $"git {string.Join(' ', arguments)} failed: {result.Error}");
    }
}
