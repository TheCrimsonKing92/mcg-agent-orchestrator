using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using static Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier;

public sealed class FocusedEvidenceUnionPlanningTests
{
    internal const string ClassA = "AcceptanceLaneStallPolicyTests";
    internal const string ClassB = "AcceptancePartitionVerdictCacheWithinAttemptRerunTests";
    internal const string MethodA = "MtpTestRunnerScriptTests.MtpScriptFilterTranslationAndFilenameBoundingMatchGateConventions";
    internal const string MethodB = "MtpTestRunnerScriptTests.BooleanGrammar_PreservesRepresentableOperatorsAndRejectsLoss";
    internal static string Fqn(string value) => "FullyQualifiedName~" + value;

    [Xunit.Theory]
    [Xunit.InlineData("|")]
    [Xunit.InlineData(";")]
    public void MixedUnion_PlansHomogeneousLaunchesAndAllTargetMappings(string separator)
    {
        var request = string.Join(separator, new[] { ClassA, ClassB, MethodA, MethodB }.Select(Fqn));
        var checks = Resolve(request, out var coverage);
        Assert.Equal(2, checks.Count);
        Assert.All(checks, check => Assert.Single(check.FocusedEvidenceTokens.Select(token => token.Kind).Distinct()));
        Assert.Equal(new[] { ClassA, ClassB, MethodA, MethodB }.Order(),
            checks.SelectMany(check => check.FocusedEvidenceTokens).Select(token => token.Value).Order());
        if (separator == "|")
            Assert.Equal(checks.Select(check => check.Name).Order(), Assert.Single(coverage.TargetToChecks).CheckNames.Order());
        else
            Assert.All(coverage.TargetToChecks, target => Assert.Single(target.CheckNames));
        Assert.All(checks, check => Assert.NotEmpty(Argv(check)));
    }

    [Xunit.Fact]
    public void Exclusions_KeepAlternativeScopeAndSharedConjunctionScope()
    {
        var checks = Resolve($"({Fqn(ClassA)}&Category!=Local)|{Fqn(MethodA)}", out _);
        Assert.Equal(2, checks.Count);
        var excluded = Assert.Single(checks, check => check.FocusedEvidenceTokens.Any(token => token.Kind == FocusedEvidenceTokenKind.ExcludedTrait));
        Assert.Contains(excluded.FocusedEvidenceTokens, token => token.Value == ClassA);
        Assert.DoesNotContain(Assert.Single(checks, check => check != excluded).FocusedEvidenceTokens,
            token => token.Kind == FocusedEvidenceTokenKind.ExcludedTrait);

        checks = Resolve($"({Fqn(ClassA)}|{Fqn(MethodA)})&Category!=Shared&FullyQualifiedName!~ExcludedTests", out _);
        Assert.Equal(2, checks.Count);
        Assert.All(checks, check =>
        {
            Assert.Contains(check.FocusedEvidenceTokens, token => token.Value == "Shared");
            Assert.Contains(check.FocusedEvidenceTokens, token => token.Value == "ExcludedTests");
            Assert.NotEmpty(Argv(check));
        });
    }

    [Xunit.Fact]
    public void HomogeneousUnions_DeduplicateTokensAndKeepOriginalTargets()
    {
        foreach (var values in new[] { new[] { ClassA, ClassB }, new[] { MethodA, MethodB } })
        {
            var checks = Resolve($"{Fqn(values[0])}|{Fqn(values[1])};{Fqn(values[0])}", out var coverage);
            var check = Assert.Single(checks);
            Assert.Equal(2, check.FocusedEvidenceTokens.Count);
            Assert.Equal(2, coverage.TargetToChecks.Count);
            Assert.All(coverage.TargetToChecks, target => Assert.Equal(check.Name, Assert.Single(target.CheckNames)));
        }
    }

    [Xunit.Fact]
    public void DuplicateExcludedAlternatives_ShareOneLaunchAndRetainBothTargets()
    {
        var checks = Resolve($"({Fqn(ClassA)}&Category!=Local);{Fqn(ClassA)}&Category!=Local", out var coverage);
        var check = Assert.Single(checks);
        Assert.Equal(2, coverage.TargetToChecks.Count);
        Assert.All(coverage.TargetToChecks, target => Assert.Equal(check.Name, Assert.Single(target.CheckNames)));
    }

    [Xunit.Fact]
    public void RepresentableConjunction_EmitsIntersectingGroupsWithSharedExclusion()
    {
        var check = Assert.Single(Resolve(
            $"({Fqn(ClassA)}|{Fqn(ClassB)})&{Fqn(MethodA)}&Category!=Local", out _));
        var argv = FilterArgv(check);
        Assert.Equal(2, argv.Count(value => value == "--filter-class"));
        Assert.Equal(1, argv.Count(value => value == "--filter-method"));
        Assert.Contains("--filter-not-trait", argv);
    }

    [Xunit.Theory]
    [Xunit.InlineData("FullyQualifiedName~A&FullyQualifiedName~B")]
    [Xunit.InlineData("FullyQualifiedName~A|Category!=Local")]
    [Xunit.InlineData("FullyQualifiedName~A||FullyQualifiedName~B")]
    [Xunit.InlineData("(FullyQualifiedName~A")]
    [Xunit.InlineData("FullyQualifiedName~A)")]
    [Xunit.InlineData("FullyQualifiedName~A&")]
    [Xunit.InlineData("FullyQualifiedName~A|Name~Method")]
    [Xunit.InlineData("FullyQualifiedName~MtpTestRunnerScriptTests.DoesNotExist")]
    [Xunit.InlineData("FullyQualifiedName~MtpTestRunnerScriptTests&FullyQualifiedName!~MtpTestRunnerScriptTests.BooleanGrammar_PreservesRepresentableOperatorsAndRejectsLoss")]
    public void UnsupportedShapes_RejectBeforeLaunch(string request)
    {
        Assert.False(TryBuildFocusedEvidenceChecks(request, new AcceptanceGateEngineSettings(),
            MtpTestRunnerScriptTests.RepositoryRoot(), out var checks, out _, out var rejection));
        Assert.Empty(checks);
        Assert.NotNull(rejection);
    }

    [Xunit.Fact]
    public void CommandConstruction_RejectsUnpartitionedMixedUnionAndLocalExclusion()
    {
        var checks = Resolve($"{Fqn(ClassA)};{Fqn(MethodA)}", out _);
        var tokens = checks.SelectMany(check => check.FocusedEvidenceTokens).ToArray();
        Assert.Throws<InvalidOperationException>(() => Argv(new AcceptanceManifestCheck
        {
            Project = AcceptancePolicyShardPlanner.InfrastructureTestsProject,
            Arguments = ["--filter", string.Join("|", tokens.Select(token => token.CanonicalToken))],
            FocusedEvidenceTokens = tokens
        }));
        checks = Resolve($"({Fqn(ClassA)}&Category!=Local)|{Fqn(ClassB)}", out _);
        tokens = checks.SelectMany(check => check.FocusedEvidenceTokens).ToArray();
        Assert.Throws<InvalidOperationException>(() => Argv(new AcceptanceManifestCheck
        {
            Project = AcceptancePolicyShardPlanner.InfrastructureTestsProject,
            Arguments = ["--filter", $"({Fqn(ClassA)}&Category!=Local)|{Fqn(ClassB)}"],
            FocusedEvidenceTokens = tokens
        }));
    }

    [Xunit.Fact]
    public void RecordedRequest_PreservesExactTokensWithoutImportingBranchImplementation()
    {
        // Source: packaged goal/timeline.json HumanInputReceived. Canonical metadata:
        // .orchestrator/pre-review-evidence-attempts/ad3fafc01dd042be82b03c1b4f4143b2/
        // ad3fafc0-0-20261012225158714-a2c41dc780bb47b98de522ca75173189.attempt.json
        const string recorded = "Infrastructure.Tests:FullyQualifiedName~AcceptanceLaneStallPolicyTests; Infrastructure.Tests:FullyQualifiedName~AcceptancePartitionVerdictCacheWithinAttemptRerunTests; Infrastructure.Tests:FullyQualifiedName~MtpTestRunnerScriptTests.ManagedMtpRunnerExecutesEveryRepositoryTestProjectAfterIsolatedBuild; Infrastructure.Tests:FullyQualifiedName~MtpTestRunnerScriptTests.NestedDiagnostics_CurrentManagedRun_ProvidesCustody; Infrastructure.Tests:FullyQualifiedName~MtpTestRunnerScriptTestsSilenceGuard";
        var root = Directory.CreateTempSubdirectory("focused-recorded-").FullName;
        try
        {
            var directory = Path.Combine(root, "tests", "Mcg.AgentOrchestrator.Infrastructure.Tests");
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "MtpTestRunnerScriptTests.cs"), """
                public class AcceptanceLaneStallPolicyTests { }
                public class AcceptancePartitionVerdictCacheWithinAttemptRerunTests { }
                public class MtpTestRunnerScriptTestsSilenceGuard { }
                public class MtpTestRunnerScriptTests
                {
                    [Xunit.Fact] public void ManagedMtpRunnerExecutesEveryRepositoryTestProjectAfterIsolatedBuild() { }
                    [Xunit.Fact] public void NestedDiagnostics_CurrentManagedRun_ProvidesCustody() { }
                }
                """);
            var checks = Resolve(recorded, out var coverage, root);
            Assert.Equal(2, checks.Count);
            Assert.Equal(5, coverage.TargetToChecks.Count);
            Assert.Equal(recorded.Split(';').Select(target => target.Trim()).Order(), coverage.TargetToChecks.Select(target => target.Target).Order());
            Assert.Equal(3, checks.Sum(check => Argv(check).Count(argument => argument == "--filter-class")));
            Assert.Equal(2, checks.Sum(check => Argv(check).Count(argument => argument == "--filter-method")));
            Assert.All(checks, check => Assert.False(Argv(check).Contains("--filter-class") && Argv(check).Contains("--filter-method")));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    internal static IReadOnlyList<AcceptanceManifestCheck> Resolve(
        string request, out FocusedEvidenceCoverage coverage, string? root = null)
    {
        Assert.True(TryBuildFocusedEvidenceChecks(request, new AcceptanceGateEngineSettings(),
            root ?? MtpTestRunnerScriptTests.RepositoryRoot(), out var checks, out coverage, out var rejection), rejection.Detail);
        return checks;
    }

    internal static string[] Argv(AcceptanceManifestCheck check, string? resultsPath = null)
    {
        var assembly = typeof(MtpTestRunnerScriptTests).Assembly.Location;
        var artifacts = Path.GetDirectoryName(assembly)!;
        var settings = new AcceptanceGateEngineSettings
        {
            MtpInvocations = [new AcceptanceMtpInvocation
            {
                Project = check.Project!,
                ExecutablePathTemplate = Path.GetFileName(assembly),
                Arguments = ["{executable}", "--results-directory", "{resultsDirectory}", "--report-trx",
                    "--report-trx-filename", "{trxFileName}"]
            }]
        };
        var environment = new DotnetBuildEnvironment("union", artifacts, artifacts, "", [], "union");
        return AcceptanceCheckCommandBuilder.BuildMtpTestArguments(check, settings, environment,
            new DotnetTestTelemetry([resultsPath ?? Path.Combine(artifacts, "union.trx")], []),
            excludeHostIntegration: false, (_, _) => throw new InvalidOperationException("Focused translation must own this launch."));
    }

    internal static string[] FilterArgv(AcceptanceManifestCheck check)
    {
        var argv = Argv(check);
        var index = Array.FindIndex(argv, argument => argument.StartsWith("--filter-", StringComparison.Ordinal));
        Assert.True(index >= 0);
        return argv[index..];
    }
}
