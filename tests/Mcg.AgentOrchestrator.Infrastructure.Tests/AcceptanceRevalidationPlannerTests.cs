using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;
using AcceptanceManifestCheck = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.AcceptanceManifestCheck;

[Xunit.Collection("IsolatedProcessSpawning")]
public sealed class AcceptanceRevalidationPlannerTests : GoalAcceptanceVerifierTestBase, IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"mcg-revalidation-{Guid.NewGuid():N}");
    private const string CandidatePath = "tests/A/ATest.cs";

    [Fact]
    public void DisjointLanding_AffectedChecksExactlyMatchChangedClosureHashes()
    {
        var verified = CreateRepository();
        Git(_root, "checkout", "-b", "candidate");
        Commit(CandidatePath, "// candidate");
        var candidate = Git(_root, "rev-parse", "HEAD");
        Git(_root, "checkout", "main");
        Commit("src/LibB/B.cs", "// later main");
        var current = Git(_root, "rev-parse", "HEAD");
        var plan = Assert.IsType<AcceptanceRevalidationPlan.Revalidate>(Plan(verified, current));
        Assert.Equal(["src/LibB/B.cs"], plan.LandedPaths);
        Assert.Equal([CandidatePath], plan.CandidatePaths);
        Assert.Equal(["b tests"], plan.AffectedChecks);
        Assert.Equal(["a tests"], plan.UnaffectedChecks);

        var before = Path.Combine(_root, "before");
        var after = Path.Combine(_root, "after");
        Git(_root, "worktree", "add", "--detach", before, candidate);
        Git(_root, "worktree", "add", "--detach", after, current);
        Git(after, "cherry-pick", candidate);
        var changed = Checks().Where(check =>
        {
            var oldHash = AcceptanceLaneClosureHasher.TryCompute(before, check);
            var newHash = AcceptanceLaneClosureHasher.TryCompute(after, check);
            Assert.NotNull(oldHash);
            Assert.NotNull(newHash);
            return oldHash != newHash;
        }).Select(check => check.Name).ToArray();
        Assert.Equal(plan.AffectedChecks, changed);
    }

    [Theory]
    [InlineData(CandidatePath, "overlap", null)]
    [InlineData("Directory.Build.props", "unsafe-area", "BuildSystem")]
    [InlineData("config/extra.json", "unsafe-area", "Configuration")]
    [InlineData(".agents/skills/probe/SKILL.md", "unsafe-area", "Skill")]
    [InlineData("src/LibB/obj/Generated.cs", "unsafe-area", "GeneratedOrNoisy")]
    [InlineData("unknown.data", "unsafe-area", "Unknown")]
    public void UnsafeOrOverlappingLanding_RequiresFullRegate(string path, string reason, string? kind)
    {
        var verified = CreateRepository();
        Commit(path, path.EndsWith(".props", StringComparison.Ordinal) ? "<Project />" : "changed");
        var plan = Assert.IsType<AcceptanceRevalidationPlan.FullRegate>(Plan(verified));
        Assert.Equal(reason, plan.Reason);
        Assert.Equal(path, plan.Path);
        Assert.Equal(kind, plan.Kind);
    }

    [Fact]
    public void Overlap_NormalizesSeparatorsAndCase()
    {
        var verified = CreateRepository();
        Commit(CandidatePath, "changed");
        var plan = Assert.IsType<AcceptanceRevalidationPlan.FullRegate>(AcceptanceRevalidationPlanner.Plan(
            _root, verified, Git(_root, "rev-parse", "HEAD"), ["/TESTS\\A\\ATEST.CS/"]));
        Assert.Equal("overlap", plan.Reason);
        Assert.Equal(CandidatePath, plan.Path);
    }

    [Fact]
    public void UnsafeCandidate_RequiresFullRegate()
    {
        var verified = CreateRepository();
        Commit("src/LibB/B.cs", "changed");
        var plan = Assert.IsType<AcceptanceRevalidationPlan.FullRegate>(AcceptanceRevalidationPlanner.Plan(
            _root, verified, Git(_root, "rev-parse", "HEAD"), ["config/extra.json"]));
        Assert.Equal("unsafe-area", plan.Reason);
        Assert.Equal("Configuration", plan.Kind);
    }

    [Fact]
    public void NonAncestorOrMissingGitEvidence_RequiresUnknownRegate()
    {
        var verified = CreateRepository();
        Git(_root, "checkout", "-b", "other");
        Commit("src/LibA/A.cs", "other branch");
        var other = Git(_root, "rev-parse", "HEAD");
        Git(_root, "checkout", "main");
        Commit("src/LibB/B.cs", "main branch");
        Assert.Equal("unknown", Assert.IsType<AcceptanceRevalidationPlan.FullRegate>(Plan(other)).Reason);
        Assert.Equal("unknown", Assert.IsType<AcceptanceRevalidationPlan.FullRegate>(Plan(new string('f', 40))).Reason);
        Assert.IsType<AcceptanceRevalidationPlan.Unchanged>(Plan(verified, verified));
    }

    [Fact]
    public void DocumentationLanding_AffectsEveryCheck()
    {
        var verified = CreateRepository();
        Commit("docs/readme.md", "later documentation");
        var plan = Assert.IsType<AcceptanceRevalidationPlan.Revalidate>(Plan(verified));
        Assert.Equal(["a tests", "b tests"], plan.AffectedChecks);
        Assert.Empty(plan.UnaffectedChecks);
    }

    [Fact]
    public void InfrastructureLanding_RevalidatesWithoutChangingSoloCarryClassifier()
    {
        var verified = CreateRepository();
        Commit("src/Mcg.AgentOrchestrator.Infrastructure/RacingLanding.cs", "// racing landing");
        var current = Git(_root, "rev-parse", "HEAD");
        var plan = Assert.IsType<AcceptanceRevalidationPlan.Revalidate>(Plan(verified, current));
        Assert.Empty(plan.AffectedChecks);
        Assert.Equal(["a tests", "b tests"], plan.UnaffectedChecks);
        var carry = Assert.IsType<AcceptanceMainAdvanceDisposition.Overlap>(AcceptanceMainAdvanceClassifier.Classify(
            verified, current, [CandidatePath], [CandidatePath], [CandidatePath], plan.LandedPaths));
        Assert.Equal("SharedInfrastructure", carry.Kind);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("malformed")]
    [InlineData("{\"checks\":[]}")]
    public void UnreadableManifest_RequiresFullRegate(string manifest)
    {
        var verified = CreateRepository();
        Commit("src/LibB/B.cs", "changed");
        Write("config/acceptance-manifest.json", manifest);
        Assert.Equal("manifest-unreadable", Assert.IsType<AcceptanceRevalidationPlan.FullRegate>(Plan(verified)).Reason);
    }

    [Fact]
    public void MembersAndHash_AgreeOnAvailabilityForCleanFixtureChecks()
    {
        CreateRepository();
        foreach (var check in Checks().Append(new AcceptanceManifestCheck { Type = "dotnet-test", Project = "tests/Missing.csproj" }))
        {
            var members = AcceptanceLaneClosureHasher.TryComputeMembers(_root, check);
            Assert.Equal(members is not null, AcceptanceLaneClosureHasher.TryCompute(_root, check) is not null);
            if (members is not null) Assert.Equal(members.Order(StringComparer.Ordinal), members);
        }
    }

    [Fact]
    public void UnreadableClosure_IsAffectedConservatively()
    {
        var verified = CreateRepository();
        Commit("src/LibB/B.cs", "changed");
        File.Delete(Path.Combine(_root, "tests/A/A.csproj"));
        var plan = Assert.IsType<AcceptanceRevalidationPlan.Revalidate>(Plan(verified));
        Assert.Equal(["a tests", "b tests"], plan.AffectedChecks);
        Assert.Empty(plan.UnaffectedChecks);
    }

    [Fact]
    public void EmptyCandidateScope_RequiresUnknownRegate()
    {
        var verified = CreateRepository();
        Commit("src/LibB/B.cs", "changed");
        var plan = Assert.IsType<AcceptanceRevalidationPlan.FullRegate>(AcceptanceRevalidationPlanner.Plan(
            _root, verified, Git(_root, "rev-parse", "HEAD"), []));
        Assert.Equal("unknown", plan.Reason);
    }

    private AcceptanceRevalidationPlan Plan(string verified, string? current = null) =>
        AcceptanceRevalidationPlanner.Plan(_root, verified, current ?? Git(_root, "rev-parse", "HEAD"), [CandidatePath]);

    private string CreateRepository()
    {
        Directory.CreateDirectory(_root);
        Git(_root, "init", "-b", "main");
        Git(_root, "config", "user.email", "tests@example.invalid");
        Git(_root, "config", "user.name", "Tests");
        foreach (var name in new[] { "A", "B" })
        {
            Write($"tests/{name}/{name}.csproj", $"<Project><ItemGroup><ProjectReference Include=\"../../src/Lib{name}/Lib{name}.csproj\" /></ItemGroup></Project>");
            Write($"src/Lib{name}/Lib{name}.csproj", "<Project />");
            Write($"src/Lib{name}/{name}.cs", "// base library");
            Write($"tests/{name}/{name}Test.cs", "// base test");
        }
        Write("config/acceptance-manifest.json", """
            {"checks":[{"name":"a tests","type":"dotnet-test","project":"tests/A/A.csproj"},
                       {"name":"b tests","type":"dotnet-test","project":"tests/B/B.csproj"}]}
            """);
        Write("docs/readme.md", "base docs");
        Git(_root, "add", ".");
        Git(_root, "commit", "-m", "base");
        return Git(_root, "rev-parse", "HEAD");
    }

    private static AcceptanceManifestCheck[] Checks() => [
        new() { Name = "a tests", Type = "dotnet-test", Project = "tests/A/A.csproj" },
        new() { Name = "b tests", Type = "dotnet-test", Project = "tests/B/B.csproj" }];

    private void Commit(string path, string contents)
    {
        Write(path, contents);
        Git(_root, "add", path);
        Git(_root, "commit", "-m", "change " + path);
    }

    private void Write(string path, string contents)
    {
        var fullPath = Path.Combine(_root, path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, contents);
    }

    private static string Git(string root, params string[] arguments)
    {
        var result = InfrastructureTestSupport.RunGitProbe(root, arguments);
        Assert.True(result.Succeeded, $"git {string.Join(' ', arguments)} exit={result.ExitCode}: {result.StandardError}");
        return result.StandardOutput.Trim();
    }

    public void Dispose() => DeleteDirectoryWithRetry(_root);
}
