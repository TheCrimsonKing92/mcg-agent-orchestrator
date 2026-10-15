using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using System.Text.Json;

public sealed class WorkerBuildProjectScopeTests
{
    [Xunit.Fact]
    public void TestsOnlyRequestIncludesCommittedStagedDirtyAndUntrackedOwners()
    {
        using var fixture = new Fixture();
        fixture.Change("Core");
        fixture.Commit();
        fixture.Change("Execution");
        fixture.Git("add", "Execution");
        fixture.Change("App");
        fixture.Write("Tests/NewTests.cs", "public class NewTests { }");

        var scope = WorkerBuildProjectScope.Resolve(fixture.Root, ["Tests/Tests.csproj"]);

        Assert.Equal(["Tests/Tests.csproj"], scope.RequestedProjects);
        Assert.Equal(Fixture.AllProjects, scope.RequiredProjects);
        Assert.Equal(Fixture.AllProjects, scope.Projects);
        var fallbacks = 0;
        var admission = OrchestratorBuildEvidenceCheck.Resolve(fixture.Root, AgentRole.Developer, [], [], false,
            () => new(true, "matched", scope.Projects), _ => { fallbacks++; return new(false, -1, "unexpected fallback"); });
        Assert.False(admission.FailsRound, admission.Diagnostic);
        Assert.Equal(0, fallbacks);
    }

    [Xunit.Fact]
    public void CorrectionPreservesPinnedBaselineAndRenameKeepsBothOwners()
    {
        using var fixture = new Fixture();
        fixture.Change("Core");
        fixture.Commit();
        var baseline = fixture.Metadata().GoalBaseline;
        // Move the integration branch forward: correction must retain the original goal boundary.
        fixture.Git("branch", "--force", "landing", "HEAD");
        WorkerBuildProjectScope.Prepare(fixture.Root, fixture.GoalId, "correction", "landing");
        fixture.Git("mv", "Tests/Tests.cs", "App/RenamedTests.cs");

        var scope = WorkerBuildProjectScope.Resolve(fixture.Root, ["Tests/Tests.csproj"]);

        Assert.Equal(baseline, fixture.Metadata().GoalBaseline);
        Assert.Equal(["App/App.csproj", "Core/Core.csproj", "Tests/Tests.csproj"], scope.Projects);
    }

    [Xunit.Theory]
    [Xunit.InlineData("dirty-restoration")]
    [Xunit.InlineData("staged-restoration")]
    [Xunit.InlineData("index-worktree-cancellation")]
    public void BaselineRestoration_RetainsDirtyOwnerWithoutAdmissionFallback(string scenario)
    {
        using var fixture = new Fixture();
        var baseline = fixture.Metadata().GoalBaseline;
        fixture.Change("Core");
        if (scenario == "index-worktree-cancellation")
            fixture.Git("add", "Core/Core.cs");
        else
            fixture.Commit();
        fixture.Git("restore", "--source=" + baseline, "--worktree", "--", "Core/Core.cs");
        if (scenario == "staged-restoration") fixture.Git("add", "Core/Core.cs");

        // The producer's previous baseline query sees nothing, but completion still has a dirty owner.
        Assert.Empty(fixture.Git("diff", "--name-only", baseline, "--", "Core/Core.cs"));
        Assert.Contains("Core/Core.cs", fixture.Git("status", "--porcelain", "--", "Core/Core.cs"), StringComparison.Ordinal);
        var scope = WorkerBuildProjectScope.Resolve(fixture.Root, ["Tests/Tests.csproj"]);
        Assert.Equal(["Core/Core.csproj"], scope.RequiredProjects);
        Assert.Equal(["Core/Core.csproj", "Tests/Tests.csproj"], scope.Projects);
        var receiptReads = 0;
        var fallbacks = 0;
        var admission = OrchestratorBuildEvidenceCheck.Resolve(fixture.Root, AgentRole.Developer, [], ["Core/Core.cs"], false,
            () => { receiptReads++; return new(true, "matched", scope.Projects); },
            _ => { fallbacks++; return new(false, -1, "unexpected fallback"); });

        Assert.False(admission.FailsRound, admission.Diagnostic);
        Assert.Equal(1, receiptReads);
        Assert.Equal(0, fallbacks);
    }

    [Xunit.Theory]
    [Xunit.InlineData("missing-project")]
    [Xunit.InlineData("outside-project")]
    [Xunit.InlineData("unresolved-source")]
    [Xunit.InlineData("missing-metadata")]
    [Xunit.InlineData("foreign-root")]
    [Xunit.InlineData("foreign-branch")]
    [Xunit.InlineData("invalid-baseline")]
    [Xunit.InlineData("nonancestor-baseline")]
    public void InvalidInputsNeverProduceCoverage(string scenario)
    {
        using var fixture = new Fixture();
        var requested = "Tests/Tests.csproj";
        switch (scenario)
        {
            case "missing-project": requested = "missing.csproj"; break;
            case "outside-project": requested = "../outside.csproj"; break;
            case "unresolved-source": fixture.Write("Unowned.cs", "public class Unowned { }"); break;
            case "missing-metadata": File.Delete(WorkerBuildProjectScope.MetadataPath(fixture.Root)); break;
            case "foreign-root": fixture.SetMetadata(fixture.Metadata() with { WorktreeRoot = Path.GetDirectoryName(fixture.Root)! }); break;
            case "foreign-branch": fixture.SetMetadata(fixture.Metadata() with { Branch = "goal/foreign" }); break;
            case "invalid-baseline": fixture.SetMetadata(fixture.Metadata() with { GoalBaseline = "landing" }); break;
            case "nonancestor-baseline":
                fixture.Git("checkout", "--orphan", "foreign");
                // Distinct trees keep same-second root commits from sharing an identity.
                fixture.Write(".foreign-history", "unrelated root");
                fixture.Commit();
                var foreign = fixture.Git("rev-parse", "HEAD").Trim();
                fixture.Git("checkout", "goal/" + fixture.Prefix);
                Assert.NotEqual(fixture.Metadata().GoalBaseline, foreign);
                var ancestry = GitCli.RunForRepository(fixture.Root, ["merge-base", "--is-ancestor", foreign, "HEAD"]);
                Assert.False(ancestry.DrainTimedOut);
                Assert.Equal(1, ancestry.ExitCode);
                fixture.SetMetadata(fixture.Metadata() with { GoalBaseline = foreign });
                break;
        }
        Assert.ThrowsAny<IOException>(() => WorkerBuildProjectScope.Resolve(fixture.Root, [requested]));
    }

    [Xunit.Fact]
    public void SourceAndDispatchMetadataChangesInvalidateCurrentIdentity()
    {
        using var fixture = new Fixture();
        var initial = WorkerBuildProjectScope.Resolve(fixture.Root, ["Tests/Tests.csproj"]);
        fixture.Change("Core");
        var changed = WorkerBuildProjectScope.Resolve(fixture.Root, ["Tests/Tests.csproj"]);
        Assert.NotEqual(initial.Identity, changed.Identity);
        WorkerBuildProjectScope.Prepare(fixture.Root, fixture.GoalId, "correction", "landing");
        var corrected = WorkerBuildProjectScope.Resolve(fixture.Root, ["Tests/Tests.csproj"]);
        Assert.Equal(changed.Projects, corrected.Projects);
        Assert.NotEqual(changed.MetadataDigest, corrected.MetadataDigest);
        Assert.NotEqual(changed.Identity, corrected.Identity);
    }

    [Xunit.Fact]
    public void ExcludedSourceDoesNotCreateAnUnresolvedBuildObligation()
    {
        using var fixture = new Fixture();
        fixture.Write("Core/Core.csproj", "<Project><ItemGroup><Compile Remove=\"Excluded.cs\" /></ItemGroup></Project>");
        fixture.Write("Core/Excluded.cs", "deliberately excluded");
        Assert.Equal(["Tests/Tests.csproj"], WorkerBuildProjectScope.Resolve(fixture.Root, ["Tests/Tests.csproj"]).Projects);
    }

    [Xunit.Theory]
    [Xunit.InlineData("$(RestoredSource)")]
    [Xunit.InlineData("@(RestoredSources)")]
    [Xunit.InlineData("%(Identity)")]
    public void UnevaluatedCompileReinclusionCannotEstablishSafeExclusion(string include)
    {
        using var fixture = new Fixture();
        fixture.Write("Core/Core.csproj", $"""
            <Project>
              <PropertyGroup><RestoredSource>Core.cs</RestoredSource></PropertyGroup>
              <ItemGroup>
                <RestoredSources Include="Core.cs" />
                <Compile Remove="Core.cs" />
                <Compile Include="{include}" />
              </ItemGroup>
            </Project>
            """);
        fixture.Change("Core");

        var error = Assert.Throws<IOException>(() => WorkerBuildProjectScope.Resolve(fixture.Root, ["Tests/Tests.csproj"]));

        Assert.Contains("unresolved compiled source: Core/Core.cs", error.Message, StringComparison.Ordinal);
    }

    [Xunit.Theory]
    [Xunit.InlineData("explicit-import")]
    [Xunit.InlineData("sdk-attribute")]
    [Xunit.InlineData("sdk-element")]
    public void ImportedReinclusion_RefusesConclusiveLocalExclusion(string scenario)
    {
        using var fixture = new Fixture();
        fixture.Write("Core/Restore.props", "<Project><ItemGroup><Compile Include=\"Core.cs\" /></ItemGroup></Project>");
        var project = scenario switch
        {
            "explicit-import" => "<Project><ItemGroup><Compile Remove=\"Core.cs\" /></ItemGroup><Import Project=\"Restore.props\" /></Project>",
            "sdk-attribute" => "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup><Compile Remove=\"Core.cs\" /></ItemGroup></Project>",
            _ => "<Project><Sdk Name=\"Microsoft.NET.Sdk\" /><ItemGroup><Compile Remove=\"Core.cs\" /></ItemGroup></Project>"
        };
        // SDK projects import Directory.Build.targets implicitly, after local Compile declarations.
        if (scenario != "explicit-import")
            fixture.Write("Core/Directory.Build.targets", "<Project><Import Project=\"Restore.props\" /></Project>");
        fixture.Write("Core/Core.csproj", project);
        fixture.Change("Core");

        var error = Assert.Throws<IOException>(() => WorkerBuildProjectScope.Resolve(fixture.Root, ["Tests/Tests.csproj"]));

        Assert.Contains("unresolved compiled source: Core/Core.cs", error.Message, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void LiteralCompileReinclusionRetainsRequiredOwner()
    {
        using var fixture = new Fixture();
        fixture.Write("Core/Core.csproj", "<Project><ItemGroup><Compile Remove=\"Core.cs\" /><Compile Include=\"Core.cs\" /></ItemGroup></Project>");
        fixture.Change("Core");

        Assert.Equal(["Core/Core.csproj", "Tests/Tests.csproj"], WorkerBuildProjectScope.Resolve(fixture.Root, ["Tests/Tests.csproj"]).Projects);
    }

    [Xunit.Fact]
    public void ValidButRewrittenBaselineCannotEraseConductorPinnedCoverage()
    {
        using var fixture = new Fixture();
        var pinned = WorkerBuildProjectScope.ReadDispatchIdentity(fixture.Root);
        fixture.Change("Core");
        fixture.Commit();
        fixture.SetMetadata(fixture.Metadata() with { GoalBaseline = fixture.Git("rev-parse", "HEAD").Trim(), DispatchBase = fixture.Git("rev-parse", "HEAD").Trim() });
        var receiptReads = 0;
        var fallbacks = 0;

        var admission = OrchestratorBuildEvidenceCheck.Resolve(fixture.Root, AgentRole.Developer, [], [], false,
            () => { receiptReads++; return new(true, "matched", ["Tests/Tests.csproj"]); },
            _ => { fallbacks++; return new(true, 0, "must not run"); }, expectedScopeMetadataDigest: pinned.MetadataDigest);

        Assert.True(admission.MissingEvidence);
        Assert.Equal(0, receiptReads);
        Assert.Equal(0, fallbacks);
        Assert.Contains("conductor-captured dispatch identity", admission.Diagnostic, StringComparison.Ordinal);
        Assert.Throws<IOException>(() => WorkerBuildProjectScope.Prepare(fixture.Root, fixture.GoalId, "correction", "landing", pinned.GoalBaseline));
    }

    [Xunit.Fact]
    public void TrackedDispatchAdoptsLegacyPrefixWithoutReplacingTheGoalBaseline()
    {
        using var fixture = new Fixture();
        var baseline = fixture.Metadata().GoalBaseline;
        fixture.SetMetadata(fixture.Metadata() with { GoalId = fixture.Prefix, TaskId = "legacy-operator" });
        fixture.Change("Core");
        fixture.Commit();

        WorkerBuildProjectScope.Prepare(fixture.Root, fixture.GoalId, "tracked-task", "landing", baseline);

        Assert.Equal(fixture.GoalId, fixture.Metadata().GoalId);
        Assert.Equal("tracked-task", fixture.Metadata().TaskId);
        Assert.Equal(baseline, fixture.Metadata().GoalBaseline);
        Assert.Equal(["Core/Core.csproj", "Tests/Tests.csproj"], WorkerBuildProjectScope.Resolve(fixture.Root, ["Tests/Tests.csproj"]).Projects);
    }

    internal sealed class Fixture : IDisposable
    {
        internal static readonly string[] AllProjects = ["App/App.csproj", "Core/Core.csproj", "Execution/Execution.csproj", "Tests/Tests.csproj"];
        internal string Root { get; }
        internal string GoalId { get; } = Guid.NewGuid().ToString("N");
        internal string Prefix => GoalId[..8];
        internal Fixture()
        {
            Root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Temp", "mcg-build-scope-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            Write(".gitignore", ".orchestrator-context/\n**/obj/\n**/bin/\n");
            foreach (var name in new[] { "Core", "Execution", "App", "Tests" })
            {
                Write($"{name}/{name}.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
                Write($"{name}/{name}.cs", $"public class {name} {{ }}");
            }
            Git("init", "--initial-branch=landing");
            Commit();
            Git("checkout", "-b", "goal/" + Prefix);
            WorkerBuildProjectScope.Prepare(Root, GoalId, "initial", "landing");
        }
        internal void Write(string path, string text)
        {
            var absolute = Path.Combine(Root, path);
            Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
            File.WriteAllText(absolute, text);
        }
        internal void Change(string name) => Write($"{name}/{name}.cs", $"public class {name} {{ public int Changed => 1; }}");
        internal void Commit(bool allowEmpty = false)
        {
            Git("add", "-A");
            var arguments = new List<string> { "-c", "user.name=Scope Fixture", "-c", "user.email=scope@example.invalid", "-c", "commit.gpgsign=false", "-c", "core.hooksPath=", "commit", "-m", "fixture" };
            if (allowEmpty) arguments.Add("--allow-empty");
            Git(arguments.ToArray());
        }
        internal string Git(params string[] arguments)
        {
            var result = GitCli.RunForRepository(Root, arguments);
            Assert.True(result.Succeeded && !result.DrainTimedOut, result.Error);
            return result.Output;
        }
        internal WorkerBuildProjectScope.Metadata Metadata() => JsonSerializer.Deserialize<WorkerBuildProjectScope.Metadata>(File.ReadAllBytes(WorkerBuildProjectScope.MetadataPath(Root)))!;
        internal void SetMetadata(WorkerBuildProjectScope.Metadata metadata) => File.WriteAllText(WorkerBuildProjectScope.MetadataPath(Root), JsonSerializer.Serialize(metadata));
        public void Dispose() => ScriptSandboxCleanup.DeleteOrThrow([Root]);
    }
}
