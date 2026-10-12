using Mcg.AgentOrchestrator.Infrastructure;
using AcceptanceManifestCheck = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.AcceptanceManifestCheck;

// Parallel-safe: the real root is read-only and the shared synthetic root is immutable after construction.
public sealed class FocusedEvidenceOverflowSyntheticRootEquivalenceTests
{
    private const string ClassPrefix = "FullyQualifiedName~";

    [Xunit.Fact]
    public void SyntheticRootPreservesRealRootChecksAndCoverageForWideRequest()
    {
        var classNames = FocusedEvidenceOverflowPackingTests.SyntheticClassNames;
        Assert.Equal(32, classNames.Count);
        var request = string.Join("; ", classNames.Select(name => "Infrastructure.Tests:" + ClassPrefix + name));
        var syntheticChecks = Resolve(FocusedEvidenceOverflowSyntheticRoot.Root, request, out var syntheticCoverage);
        var realChecks = Resolve(InfrastructureTestSupport.FindRepositoryRoot(), request, out var realCoverage);

        Assert.Equal(realChecks.Count, syntheticChecks.Count);
        for (var index = 0; index < realChecks.Count; index++)
        {
            var real = realChecks[index];
            var synthetic = syntheticChecks[index];
            Assert.Equal(real.Name, synthetic.Name);
            Assert.Equal(real.Project, synthetic.Project);
            Assert.Equal(real.Type, synthetic.Type);
            Assert.Equal(real.Runner, synthetic.Runner);
            Assert.Equal(real.Arguments, synthetic.Arguments);
            Assert.Equal(real.FocusedEvidenceTokens.Select(token => token.CanonicalToken),
                synthetic.FocusedEvidenceTokens.Select(token => token.CanonicalToken));
            Assert.Equal(SelectionTexts(real), SelectionTexts(synthetic));
            Assert.Equal(real.FocusedEvidenceMatchedClasses.Select(match => match.CanonicalToken),
                synthetic.FocusedEvidenceMatchedClasses.Select(match => match.CanonicalToken));
            Assert.Equal(real.FocusedEvidenceTokens.Select(token => token.CanonicalToken),
                real.FocusedEvidenceMatchedClasses.Select(match => match.CanonicalToken));
            Assert.All(real.FocusedEvidenceMatchedClasses, match =>
            {
                Assert.StartsWith(ClassPrefix, match.CanonicalToken);
                Assert.Contains(match.CanonicalToken[ClassPrefix.Length..], match.ClassNames);
            });
        }

        Assert.Equal(realCoverage.ExecutionMode, syntheticCoverage.ExecutionMode);
        Assert.Equal(realCoverage.ExecutionReason, syntheticCoverage.ExecutionReason);
        Assert.Equal(realCoverage.TargetToChecks.Select(mapping => mapping.Target),
            syntheticCoverage.TargetToChecks.Select(mapping => mapping.Target));
        for (var index = 0; index < realCoverage.TargetToChecks.Count; index++)
        {
            Assert.Equal(realCoverage.TargetToChecks[index].CheckNames, syntheticCoverage.TargetToChecks[index].CheckNames);
        }
    }

    private static IReadOnlyList<AcceptanceManifestCheck> Resolve(
        string root, string request, out FocusedEvidenceCoverage coverage)
    {
        var accepted = GoalAcceptanceVerifier.TryBuildFocusedEvidenceChecks(
            request, new AcceptanceGateEngineSettings(), root,
            out var checks, out coverage, out var rejection);
        Assert.True(accepted, $"{rejection.Code}: {rejection.Detail}");
        Assert.NotEmpty(checks);
        return checks;
    }

    private static IEnumerable<string> SelectionTexts(AcceptanceManifestCheck check) =>
        check.FocusedEvidenceSelections.Select(selection => string.Join('|', selection.Select(token => token.CanonicalToken)));
}

internal static class FocusedEvidenceOverflowSyntheticRoot
{
    private static readonly Lazy<string> SyntheticRoot = new(CreateRoot, LazyThreadSafetyMode.ExecutionAndPublication);

    internal static string Root => SyntheticRoot.Value;

    private static string CreateRoot()
    {
        // Assembly temp reclamation owns this shared root; individual tests must not delete it.
        var root = InfrastructureTestSupport.CreateTempDirectory();
        var relativeProject = AcceptancePolicyShardPlanner.InfrastructureTestsProject;
        var projectPath = Path.Combine(root, relativeProject.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(projectPath)!);
        File.WriteAllText(projectPath, File.ReadAllText(Path.Combine(
            InfrastructureTestSupport.FindRepositoryRoot(), relativeProject.Replace('/', Path.DirectorySeparatorChar))));
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(projectPath)!, "SyntheticTests.cs"),
            string.Join(Environment.NewLine, FocusedEvidenceOverflowPackingTests.SyntheticClassNames
                .Select(name => $"public sealed class {name} {{ }}")));
        return root;
    }
}
