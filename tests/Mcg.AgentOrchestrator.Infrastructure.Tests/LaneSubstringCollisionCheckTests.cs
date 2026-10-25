using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: pure rule inputs and per-test temporary roots; no processes or shared state.
public sealed class LaneSubstringCollisionCheckTests
{
    private const string IncidentClass = "WorkerBuildRequestQosOptOutTests";
    private const string FixturePath = "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/" + IncidentClass + ".cs";
    private const string PassMessage = "source size ratchet and acceptance manifest lane consistency checks passed";
    private const string Manifest = """
        { "version": 1, "engine": { "infrastructureTestLanes": [
          { "name": "Dotnet build slots", "filter": "FullyQualifiedName~ConductorBatchLoopTests", "ownedCollections": ["DotnetBuildSlots"] },
          { "name": "Isolated process spawning B", "filter": "(FullyQualifiedName~PatchEquivalenceIntegrationMergeTests)&Category!=HostIntegration" },
          { "name": "Remainder balance C", "filter": "FullyQualifiedName~WorkerBuildRequest|FullyQualifiedName~ExitReconcileBuild" },
          { "name": "Remainder", "filter": "FullyQualifiedName!~ConductorBatchLoopTests&FullyQualifiedName!~PatchEquivalenceIntegrationMergeTests&FullyQualifiedName!~WorkerBuildRequest&FullyQualifiedName!~ExitReconcileBuild&Category!=HostIntegration" }
        ] } }
        """;

    [Fact]
    public void Find_IncidentOne_ReportsLaneFilterAndRepair()
    {
        var violation = Assert.Single(Find(IncidentClass));

        Assert.Equal("lane-substring-collision", violation.Rule);
        Assert.Equal(IncidentClass, violation.ClassFullName);
        Assert.Null(violation.Collection);
        Assert.Equal(["Remainder"], violation.BaselineLanes);
        Assert.Equal(["Remainder balance C"], violation.ResolvedLanes);
        Assert.Contains("'FullyQualifiedName~WorkerBuildRequest'", violation.RepairHint);
        Assert.Contains("Remainder balance C", violation.RepairHint);
        Assert.Contains("Rename the class so that no non-Remainder lane substring selects it", violation.RepairHint);
        Assert.Contains("owner-approved filter in config/acceptance-manifest.json", violation.RepairHint);
        Assert.Contains("pinned membership test", violation.RepairHint);
    }

    [Fact]
    public void Find_UnownedCollection_ReportsIncidentTwo()
    {
        const string name = "PatchEquivalenceIntegrationMergeTestsAutoRebind";

        var violation = Assert.Single(Find(name, "IsolatedProcessSpawning"));

        Assert.Equal("lane-substring-collision", violation.Rule);
        Assert.Equal(name, violation.ClassFullName);
        Assert.Equal("IsolatedProcessSpawning", violation.Collection);
        Assert.Equal(["Isolated process spawning B"], violation.ResolvedLanes);
        Assert.Contains("FullyQualifiedName~PatchEquivalenceIntegrationMergeTests", violation.RepairHint);
    }

    [Fact]
    public void Find_CandidateAddsSelectingFilter_ExemptsClass()
    {
        var candidate = Manifest.Replace("FullyQualifiedName~WorkerBuildRequest|FullyQualifiedName~ExitReconcileBuild",
            "FullyQualifiedName~WorkerBuildRequest|FullyQualifiedName~ExitReconcileBuild|FullyQualifiedName~" + IncidentClass,
            StringComparison.Ordinal);

        Assert.Empty(Find(IncidentClass, candidateManifest: candidate));
        Assert.Single(Find(IncidentClass));
    }

    [Fact]
    public void Find_RemainderClass_DoesNotReportCollision()
    {
        Assert.Empty(Find("QuokkaHarborProbeTests"));
    }

    [Fact]
    public void Find_RenamedRepair_DoesNotReportCollision()
    {
        const string repairedName = "WorkerBuildChannelQosOptOutTests";

        Assert.Empty(Find(repairedName, diff: AddedDiff(repairedName, IncidentClass)));
    }

    [Fact]
    public void Find_OwnedCollection_ExemptsOnlyTheOwnedCollection()
    {
        const string name = "ConductorBatchLoopTestsSplitFixture";

        Assert.Empty(Find(name, "DotnetBuildSlots"));
        var violation = Assert.Single(Find(name));
        Assert.Equal(["Dotnet build slots"], violation.ResolvedLanes);
        Assert.Single(Find(name, "dotnetbuildslots"));
    }

    [Fact]
    public void Find_MovedClass_DoesNotReportCollision()
    {
        Assert.Empty(Find(IncidentClass, diff: AddedDiff(IncidentClass, IncidentClass)));
    }

    [Fact]
    public void Find_ExistingClassAbsentFromDiff_DoesNotReportCollision()
    {
        Assert.Empty(Find("WorkerBuildRequestScopeBindingTests", diff: AddedDiff(IncidentClass)));
    }

    [Theory]
    [InlineData("Example.WorkerBuildRequestQosOptOutTests")]
    [InlineData("Example.Container+WorkerBuildRequestQosOptOutTests")]
    public void Find_QualifiedName_UsesSimpleDeclarationName(string fullName)
    {
        var violation = Assert.Single(Find(fullName, diff: AddedDiff(IncidentClass)));

        Assert.Equal(fullName, violation.ClassFullName);
    }

    [Theory]
    [InlineData("WorkerBuildRequestQosOptOutTestsLonger")]
    [InlineData("workerBuildRequestQosOptOutTests")]
    public void Find_DifferentDeclarationName_DoesNotTreatDescriptorAsNew(string addedName)
    {
        Assert.Empty(Find(IncidentClass, diff: AddedDiff(addedName)));
    }

    [Fact]
    public void Find_DifferentRemovedName_DoesNotExemptNewClass()
    {
        Assert.Single(Find(IncidentClass, diff: AddedDiff(IncidentClass, IncidentClass + "Longer")));
    }

    [Fact]
    public void Find_DiffHeadersAndContext_DoNotDeclareNewClasses()
    {
        var declaration = "public sealed " + "class " + IncidentClass;

        Assert.Empty(Find(IncidentClass, diff: "+++ " + declaration + "\n " + declaration));
        Assert.Single(Find(IncidentClass, diff: "--- " + declaration + "\n" + AddedDiff(IncidentClass)));
    }

    [Fact]
    public void Find_CasingOnlyLaneOrFilterEdit_DoesNotExemptClass()
    {
        var candidate = Manifest.Replace("Remainder balance C", "remainder BALANCE c", StringComparison.Ordinal)
            .Replace("~WorkerBuildRequest", "~workerbuildrequest", StringComparison.Ordinal);

        Assert.Single(Find(IncidentClass, candidateManifest: candidate));
    }

    [Fact]
    public void Find_NewLaneOrExactFilter_DoesNotReportCollision()
    {
        var newLane = Manifest.Replace("Remainder balance C", "New dedicated lane", StringComparison.Ordinal);
        var exactFilter = Manifest.Replace("FullyQualifiedName~WorkerBuildRequest|FullyQualifiedName~ExitReconcileBuild",
            "FullyQualifiedName=" + IncidentClass, StringComparison.Ordinal);

        Assert.Empty(Find(IncidentClass, candidateManifest: newLane));
        Assert.Empty(Find(IncidentClass, candidateManifest: exactFilter));
    }

    [Fact]
    public void Find_MultipleMatchingFilters_QuotesEveryFilter()
    {
        const string name = "WorkerBuildRequestExitReconcileBuildTests";

        var violation = Assert.Single(Find(name));

        Assert.Contains("'FullyQualifiedName~WorkerBuildRequest'", violation.RepairHint);
        Assert.Contains("'FullyQualifiedName~ExitReconcileBuild'", violation.RepairHint);
    }

    [Fact]
    public void Find_ZeroOrMultipleLanes_LeavesPartitionGuardInCharge()
    {
        var zero = Manifest.Replace("FullyQualifiedName~WorkerBuildRequest|FullyQualifiedName~ExitReconcileBuild",
            "FullyQualifiedName~Unrelated", StringComparison.Ordinal);
        var multiple = Manifest.Replace("FullyQualifiedName!~WorkerBuildRequest&", "", StringComparison.Ordinal);

        Assert.Empty(Find(IncidentClass, candidateManifest: zero));
        Assert.Empty(Find(IncidentClass, candidateManifest: multiple));
    }

    [Fact]
    public void Evaluate_ReadBaseStub_ReportsCollisionWithRelativeSourcePath()
    {
        using var fixture = new Fixture();
        var calls = 0;

        var result = DeveloperCompletionStructuralPreflight.Evaluate(fixture.Root, null, readBase: path =>
        {
            Assert.Equal(fixture.Root, path);
            calls++;
            return new LaneSubstringCollisionBase(Manifest, AddedDiff(IncidentClass));
        });

        Assert.Equal(1, calls);
        Assert.True(result.HasViolation);
        Assert.Contains("[lane-substring-collision]", result.Message);
        Assert.Contains(FixturePath, result.Message);
        Assert.Contains(IncidentClass, result.Message);
        Assert.Contains("Remainder balance C", result.Message);
    }

    [Fact]
    public void Evaluate_NullBaseOrDefaultCall_PreservesPassMessage()
    {
        using var fixture = new Fixture();
        var calls = 0;

        var noBase = DeveloperCompletionStructuralPreflight.Evaluate(fixture.Root, null, readBase: _ =>
        {
            calls++;
            return null;
        });
        var defaultCall = DeveloperCompletionStructuralPreflight.Evaluate(fixture.Root);

        Assert.Equal(1, calls);
        Assert.False(noBase.HasViolation);
        Assert.Equal(PassMessage, noBase.Message);
        Assert.False(defaultCall.HasViolation);
        Assert.Equal(PassMessage, defaultCall.Message);
    }

    [Theory]
    [InlineData("{ malformed")]
    [InlineData("{\"engine\":{\"infrastructureTestLanes\":[{\"name\":\"\",\"filter\":\"\"}]}}")]
    public void Evaluate_InvalidBaseManifest_PreservesExistingGuards(string baseManifest)
    {
        using var fixture = new Fixture();

        var result = DeveloperCompletionStructuralPreflight.Evaluate(fixture.Root, null,
            readBase: _ => new LaneSubstringCollisionBase(baseManifest, AddedDiff(IncidentClass)));

        Assert.False(result.HasViolation);
        Assert.Equal(PassMessage, result.Message);
        File.Delete(Path.Combine(fixture.Root, TestCollectionGuardRules.ExemptionsRelativePath));
        var guarded = DeveloperCompletionStructuralPreflight.Evaluate(fixture.Root, null,
            readBase: _ => new LaneSubstringCollisionBase(baseManifest, AddedDiff(IncidentClass)));
        Assert.True(guarded.HasViolation);
        Assert.Contains("[isolated-collection]", guarded.Message);
    }

    [Fact]
    public void Evaluate_ProjectHomeManifest_DoesNotReadWorktreeBase()
    {
        using var fixture = new Fixture();
        using var projectHome = new Fixture();
        File.WriteAllText(Path.Combine(projectHome.Root, "acceptance-manifest.json"), Manifest);

        var result = DeveloperCompletionStructuralPreflight.Evaluate(fixture.Root, projectHome.Root,
            readBase: _ => throw new InvalidOperationException("Project-home manifest must skip the base reader."));

        Assert.False(result.HasViolation);
        Assert.Equal(PassMessage, result.Message);
    }

    private static IReadOnlyList<TestCollectionGuardViolation> Find(string name, string? collection = null,
        string? candidateManifest = null, string? diff = null) =>
        LaneSubstringCollisionCheck.Find(AcceptanceGateEngineSettings.Parse(Manifest).InfrastructureTestLanes,
            AcceptanceGateEngineSettings.Parse(candidateManifest ?? Manifest).InfrastructureTestLanes,
            [new AcceptanceTestClassDescriptor(name, collection)], diff ?? AddedDiff(name));

    private static string AddedDiff(string name, string? removedName = null)
    {
        var path = "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/" + name + ".cs";
        return $"diff --git a/{path} b/{path}\nnew file mode 100644\n--- /dev/null\n+++ b/{path}\n@@ -0,0 +1,3 @@\n" +
            (removedName is null ? "" : "-public sealed " + "class " + removedName + "\n") +
            "+public sealed " + "class " + name + "\n+{\n+}\n";
    }

    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = ConductorDriverTests.CreateTempDirectory();

        internal Fixture()
        {
            Directory.CreateDirectory(Path.Combine(Root, "config"));
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(Root, FixturePath))!);
            File.WriteAllText(Path.Combine(Root, "config", "acceptance-manifest.json"), Manifest);
            File.Copy(Path.Combine(VerifiedRepositoryRoot.Find(), TestCollectionGuardRules.ExemptionsRelativePath),
                Path.Combine(Root, TestCollectionGuardRules.ExemptionsRelativePath));
            var authority = Path.Combine(Root, SourceSizeRatchet.SourcePath);
            Directory.CreateDirectory(Path.GetDirectoryName(authority)!);
            File.WriteAllText(authority, "new SourceSizeCeiling(\"guarded.cs\", 3)");
            File.WriteAllLines(Path.Combine(Root, "guarded.cs"), ["line", "line", "line"]);
            File.WriteAllText(Path.Combine(Root, FixturePath), "public sealed " + "class " + IncidentClass +
                " { [Xunit.Fact] public void Example() { } }");
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
