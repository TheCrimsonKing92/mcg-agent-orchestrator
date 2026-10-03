using Mcg.AgentOrchestrator.Infrastructure;

public sealed class FocusedEvidenceMatchedClassBudgetTests
{
    [Fact]
    public void FamilyTermsRecordEightClassesAndUseManifestBudget()
    {
        using var fixture = new Fixture();
        fixture.Write("Family.cs", """
            namespace Fixture;
            public partial class CliCommandTests { }
            public class CliCommandTestsDelta { }
            public class CliCommandTestsBeta { }
            public class CliCommandTestsAlpha { }
            public class CliCommandTestsGamma { }
            public class CliHelpTestsExtra { }
            public class CliHelpTests { }
            public class CliOwnerDigestCommandReworkByCauseTests { }
            """);
        fixture.Write("Partial.cs", "namespace Fixture; public partial class CliCommandTests { }");
        const string filter = "FullyQualifiedName~CliCommandTests|FullyQualifiedName~CliHelpTests|FullyQualifiedName~CliOwnerDigestCommandReworkByCauseTests";

        var check = fixture.Build(filter);

        Assert.Null(check.TimeoutMinutes);
        Assert.Equal(["--verbosity", "minimal", "--filter", filter], check.Arguments);
        Assert.Collection(check.FocusedEvidenceMatchedClasses,
            selection => AssertSelection(selection, "CliCommandTests", "CliCommandTests", "CliCommandTestsAlpha", "CliCommandTestsBeta", "CliCommandTestsDelta", "CliCommandTestsGamma"),
            selection => AssertSelection(selection, "CliHelpTests", "CliHelpTests", "CliHelpTestsExtra"),
            selection => AssertSelection(selection, "CliOwnerDigestCommandReworkByCauseTests", "CliOwnerDigestCommandReworkByCauseTests"));
    }

    [Fact]
    public void UniqueClassTermsKeepShortBudgetAndFilter()
    {
        using var fixture = new Fixture();
        fixture.Write("Unique.cs", "namespace Fixture; public class AlphaTests { } public class BetaTests { } public class GammaTests { }");
        const string filter = "FullyQualifiedName~AlphaTests|FullyQualifiedName~BetaTests|FullyQualifiedName~GammaTests";

        var check = fixture.Build(filter);

        Assert.Equal(10, check.TimeoutMinutes);
        Assert.Equal(["--verbosity", "minimal", "--filter", filter], check.Arguments);
        Assert.Equal("reviewer focused evidence: Infrastructure.Tests " + filter, check.Name);
        Assert.Collection(check.FocusedEvidenceMatchedClasses,
            selection => AssertSelection(selection, "AlphaTests", "AlphaTests"),
            selection => AssertSelection(selection, "BetaTests", "BetaTests"),
            selection => AssertSelection(selection, "GammaTests", "GammaTests"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingDeclarationsCountOncePerTermAndKeepResolution(bool hasNonMatchingSource)
    {
        using var fixture = new Fixture();
        if (hasNonMatchingSource)
        {
            fixture.Write("Other.cs", "public class OtherTests { }");
        }
        const string fourTerms = "FullyQualifiedName~AlphaTests|FullyQualifiedName~BetaTests|FullyQualifiedName~GammaTests|FullyQualifiedName~DeltaTests";

        var shortCheck = fixture.Build(fourTerms);
        var wideCheck = fixture.Build(fourTerms + "|FullyQualifiedName~EpsilonTests");

        Assert.Equal(10, shortCheck.TimeoutMinutes);
        Assert.Null(wideCheck.TimeoutMinutes);
        Assert.Equal(4, shortCheck.FocusedEvidenceMatchedClasses.Count);
        Assert.Equal(5, wideCheck.FocusedEvidenceMatchedClasses.Count);
        Assert.All(wideCheck.FocusedEvidenceMatchedClasses, selection => Assert.Empty(selection.ClassNames));
        Assert.Equal(["--verbosity", "minimal", "--filter", fourTerms], shortCheck.Arguments);
        Assert.False(GoalAcceptanceVerifier.TryBuildFocusedEvidenceChecks(
            "Infrastructure.Tests: FullyQualifiedName~Fixture.MissingTests", new AcceptanceGateEngineSettings(), fixture.Root,
            out var checks, out _, out var rejection));
        Assert.Empty(checks);
        Assert.Equal(FocusedEvidenceRejectionCode.UnresolvableSelection, rejection.Code);
        Assert.Equal("focused evidence selection ' FullyQualifiedName~Fixture.MissingTests' does not resolve to a class or method in Infrastructure.Tests", rejection.Detail);
    }

    [Fact]
    public void UnreadableSourceKeepsOneTargetPerTerm()
    {
        using var fixture = new Fixture();
        var path = fixture.Write("Locked.cs", "public class FamilyTestsOne { } public class FamilyTestsTwo { } public class FamilyTestsThree { } public class FamilyTestsFour { } public class FamilyTestsFive { }");
        using var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var check = fixture.Build("FullyQualifiedName~FamilyTests");

        Assert.Equal(10, check.TimeoutMinutes);
        Assert.Collection(check.FocusedEvidenceMatchedClasses, selection => AssertSelection(selection, "FamilyTests"));
    }

    [Fact]
    public void OverlappingTermsCountDistinctQualifiedClasses()
    {
        using var fixture = new Fixture();
        fixture.Write("Overlap.cs", "public class FamilyTestsA { } public class FamilyTestsB { } public class FamilyTestsC { } public class FamilyTestsD { }");

        var check = fixture.Build("FullyQualifiedName~FamilyTests|FullyQualifiedName~FamilyTestsA");

        Assert.Equal(10, check.TimeoutMinutes);
        Assert.Collection(check.FocusedEvidenceMatchedClasses,
            selection => AssertSelection(selection, "FamilyTests", "FamilyTestsA", "FamilyTestsB", "FamilyTestsC", "FamilyTestsD"),
            selection => AssertSelection(selection, "FamilyTestsA", "FamilyTestsA"));
    }

    [Fact]
    public void SameSimpleNameInDifferentNamespacesCountsEachClass()
    {
        using var fixture = new Fixture();
        fixture.Write("Namespaces.cs", """
            namespace A { public class FamilyTests { } }
            namespace B { public class FamilyTests { } }
            namespace C { public class FamilyTests { } }
            namespace D { public class FamilyTests { } }
            namespace E { public class FamilyTests { } }
            """);

        var check = fixture.Build("FullyQualifiedName~FamilyTests");

        Assert.Null(check.TimeoutMinutes);
        Assert.Collection(check.FocusedEvidenceMatchedClasses, selection => AssertSelection(selection, "FamilyTests", "FamilyTests"));
    }

    [Fact]
    public void NestedAbstractAndRecordDeclarationsCountButExclusionsDoNotSubtract()
    {
        using var fixture = new Fixture();
        fixture.Write("Kinds.cs", """
            namespace Fixture;
            public class Container { public class FamilyTestsNested { } }
            public abstract class FamilyTestsAbstract { }
            public record FamilyTestsRecord;
            public class FamilyTestsOne { }
            public class FamilyTestsTwo { }
            public interface FamilyTestsInterface { }
            public struct FamilyTestsStruct { }
            """);

        var check = fixture.Build("FullyQualifiedName~FamilyTests&FullyQualifiedName!~FamilyTestsTwo");

        Assert.Null(check.TimeoutMinutes);
        Assert.Collection(check.FocusedEvidenceMatchedClasses, selection => AssertSelection(selection, "FamilyTests",
            "FamilyTestsAbstract", "FamilyTestsNested", "FamilyTestsOne", "FamilyTestsRecord", "FamilyTestsTwo"));
    }

    [Fact]
    public void GeneratedSourcesAreExcludedAndNoCrossCallCacheIsUsed()
    {
        using var fixture = new Fixture();
        fixture.Write("Tests.cs", "public class FamilyTestsOne { }");
        fixture.Write("bin/Generated.cs", "public class FamilyTestsBin { }");
        fixture.Write("obj/Generated.cs", "public class FamilyTestsObj { }");
        Assert.Equal(10, fixture.Build("FullyQualifiedName~FamilyTests").TimeoutMinutes);
        fixture.Write("More.cs", "public class FamilyTestsTwo { } public class FamilyTestsThree { } public class FamilyTestsFour { } public class FamilyTestsFive { }");

        var check = fixture.Build("FullyQualifiedName~FamilyTests");

        Assert.Null(check.TimeoutMinutes);
        Assert.Collection(check.FocusedEvidenceMatchedClasses, selection => AssertSelection(selection, "FamilyTests",
            "FamilyTestsFive", "FamilyTestsFour", "FamilyTestsOne", "FamilyTestsThree", "FamilyTestsTwo"));
    }

    [Fact]
    public void QualifiedClassAndMethodTermsKeepTheirBudgetAndSelections()
    {
        using var fixture = new Fixture();
        fixture.Write("Qualified.cs", "namespace Fixture; public class AlphaTests { [Xunit.Fact] public void Runs() { } }");
        const string filter = "FullyQualifiedName~Fixture.AlphaTests|FullyQualifiedName~Fixture.AlphaTests.Runs";

        var check = fixture.Build(filter);

        Assert.Equal(10, check.TimeoutMinutes);
        Assert.Equal(["--verbosity", "minimal", "--filter", filter], check.Arguments);
        Assert.Collection(check.FocusedEvidenceMatchedClasses, selection => AssertSelection(selection, "Fixture.AlphaTests", "AlphaTests"));
        Assert.Equal([GoalAcceptanceVerifier.FocusedEvidenceTokenKind.Class, GoalAcceptanceVerifier.FocusedEvidenceTokenKind.Method],
            check.FocusedEvidenceTokens.Select(token => token.Kind));
    }

    [Fact]
    public void RepeatedSingleClassItemsDoNotReduceHistoricalBudget()
    {
        using var fixture = new Fixture();
        fixture.Write("Only.cs", "public class OnlyTests { }");
        var request = string.Join("; ", Enumerable.Repeat("Infrastructure.Tests: FullyQualifiedName~OnlyTests", 5));

        Assert.True(GoalAcceptanceVerifier.TryBuildFocusedEvidenceChecks(
            request, new AcceptanceGateEngineSettings(), fixture.Root, out var checks, out _, out var rejection), rejection.Detail);

        var check = Assert.Single(checks);
        Assert.Null(check.TimeoutMinutes);
        Assert.Equal(5, check.FocusedEvidenceMatchedClasses.Count);
        Assert.All(check.FocusedEvidenceMatchedClasses, selection => AssertSelection(selection, "OnlyTests", "OnlyTests"));
    }

    [Fact]
    public void ReadableProjectStillExpandsWhenAnotherProjectHasNoDeclarations()
    {
        using var fixture = new Fixture();
        fixture.Write("Family.cs", "public class FamilyTestsA { } public class FamilyTestsB { } public class FamilyTestsC { } public class FamilyTestsD { }");

        Assert.True(GoalAcceptanceVerifier.TryBuildFocusedEvidenceChecks(
            "Infrastructure.Tests: FullyQualifiedName~FamilyTests; Core.Tests: FullyQualifiedName~MissingTests",
            new AcceptanceGateEngineSettings(), fixture.Root, out var checks, out _, out var rejection), rejection.Detail);

        Assert.Equal(2, checks.Count);
        Assert.All(checks, check => Assert.Null(check.TimeoutMinutes));
        Assert.Collection(checks[0].FocusedEvidenceMatchedClasses, selection => AssertSelection(selection, "FamilyTests",
            "FamilyTestsA", "FamilyTestsB", "FamilyTestsC", "FamilyTestsD"));
        Assert.Collection(checks[1].FocusedEvidenceMatchedClasses, selection => AssertSelection(selection, "MissingTests"));
    }

    private static void AssertSelection(FocusedEvidenceMatchedClassSelection selection, string term, params string[] names)
    {
        Assert.Equal("FullyQualifiedName~" + term, selection.CanonicalToken);
        Assert.Equal(names, selection.ClassNames);
    }

    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = InfrastructureTestSupport.CreateTempDirectory();

        internal string Write(string file, string source)
        {
            var path = Path.Combine(Root, "tests", "Mcg.AgentOrchestrator.Infrastructure.Tests", file.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(Path.Combine(Root, "tests", "Mcg.AgentOrchestrator.Infrastructure.Tests", "Mcg.AgentOrchestrator.Infrastructure.Tests.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\" />");
            File.WriteAllText(path, source);
            return path;
        }

        internal GoalAcceptanceVerifier.AcceptanceManifestCheck Build(string filter)
        {
            Assert.True(GoalAcceptanceVerifier.TryBuildFocusedEvidenceChecks(
                "Infrastructure.Tests: " + filter, new AcceptanceGateEngineSettings(), Root,
                out var checks, out _, out var rejection), rejection.Detail);
            return Assert.Single(checks);
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
