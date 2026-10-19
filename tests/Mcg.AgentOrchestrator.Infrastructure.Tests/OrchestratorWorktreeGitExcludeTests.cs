using System.Text.Json;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: every test owns its repository, linked worktree and local git configuration.
public sealed class OrchestratorWorktreeGitExcludeTests
{
    [Fact]
    public void Prepare_RepeatedCalls_HidesRootArtifactsInLinkedWorktree()
    {
        using var fixture = new RepositoryFixture();
        Assert.True(File.Exists(Path.Combine(fixture.Linked, ".git")));
        Assert.Equal(RepositoryFixture.ResolveExclude(fixture.Target), fixture.ExcludePath);
        fixture.Prepare();
        fixture.Prepare();

        foreach (var entry in new[] { ".mcg-sandbox/", "/.mcg-low-integrity-v1",
                     "/.mcg-sandbox-prep-receipt-v1.json", "/.orchestrator-handoff.md" })
            AssertEntryOnce(fixture.ExcludePath, entry);

        fixture.WriteRootArtifacts("A");
        var sandbox = Path.Combine(fixture.Linked, ".mcg-sandbox");
        Directory.CreateDirectory(sandbox);
        File.WriteAllText(Path.Combine(sandbox, "marker"), "scratch");
        Assert.Empty(fixture.Git("status", "--porcelain=v1", "--untracked-files=all").Trim());
    }

    [Fact]
    public void Digest_PreparedWorktree_StaysEqualAcrossArtifactWritesAndRewrites()
    {
        using var fixture = new RepositoryFixture();
        fixture.Prepare();
        var baseline = fixture.Digest();

        fixture.WriteRootArtifacts("A");
        Assert.Equal(baseline, fixture.Digest());
        fixture.WriteRootArtifacts("B");
        Assert.Equal(baseline, fixture.Digest());
    }

    [Fact]
    public void Digest_UnpreparedWorktree_ChangesWhenRootHandoffIsWritten()
    {
        using var fixture = new RepositoryFixture();
        var baseline = fixture.Digest();

        File.WriteAllText(Path.Combine(fixture.Linked, OrchestratorWorktreeGitExclude.HandoffFileName), "handoff");

        Assert.NotEqual(baseline, fixture.Digest());
    }

    [Theory]
    [InlineData(true, true, "matched")]
    [InlineData(false, false, "digest-mismatch")]
    public void Receipt_ArtifactChanges_MatchOnlyAfterPreparation(
        bool prepare, bool expectedMatches, string expectedReason)
    {
        using var fixture = new RepositoryFixture();
        if (prepare)
            fixture.Prepare();
        var receiptPath = fixture.WriteReceipt(fixture.Digest());

        fixture.WriteRootArtifacts("A");
        fixture.WriteRootArtifacts("B");
        var verdict = WorkerBuildReceipt.Evaluate(receiptPath, fixture.Linked);

        Assert.Equal(expectedMatches, verdict.Matches);
        Assert.Equal(expectedReason, verdict.Reason);
    }

    [Fact]
    public void Digest_SourceAndNestedHandoffChanges_ReturnToBaselineWhenRestored()
    {
        using var fixture = new RepositoryFixture();
        fixture.Prepare();
        var baseline = fixture.Digest();
        var tracked = Path.Combine(fixture.Linked, "src", "App.cs");
        var original = File.ReadAllBytes(tracked);
        File.WriteAllText(tracked, "class App { public const int Value = 1; }\n");
        Assert.NotEqual(baseline, fixture.Digest());
        File.WriteAllBytes(tracked, original);
        Assert.Equal(baseline, fixture.Digest());

        var added = Path.Combine(fixture.Linked, "src", "New.cs");
        File.WriteAllText(added, "class New { }\n");
        Assert.NotEqual(baseline, fixture.Digest());
        File.Delete(added);
        Assert.Equal(baseline, fixture.Digest());

        var docs = Path.Combine(fixture.Linked, "docs");
        Directory.CreateDirectory(docs);
        var nested = Path.Combine(docs, OrchestratorWorktreeGitExclude.HandoffFileName);
        File.WriteAllText(nested, "source documentation");
        Assert.NotEqual(baseline, fixture.Digest());
        File.Delete(nested);
        Assert.Equal(baseline, fixture.Digest());
    }

    [Fact]
    public void Apply_UnanchoredHandFix_AppendsOnlyAnchoredEntriesOnce()
    {
        using var fixture = new RepositoryFixture();
        const string seed = ".mcg-low-integrity-v1\n.mcg-sandbox-prep-receipt-v1.json\n"
            + ".orchestrator-handoff.md\n.mcg-sandbox/\n";
        fixture.SeedExclude(seed);

        Assert.True(OrchestratorWorktreeGitExclude.Apply(fixture.Linked));

        Assert.Equal(seed + string.Join('\n', OrchestratorWorktreeGitExclude.DispatchArtifactEntries) + "\n",
            File.ReadAllText(fixture.ExcludePath));
        var first = File.ReadAllBytes(fixture.ExcludePath);
        Assert.True(OrchestratorWorktreeGitExclude.Apply(fixture.Linked));
        Assert.Equal(first, File.ReadAllBytes(fixture.ExcludePath));
    }

    [Fact]
    public void Apply_LegacyComment_PreservesSandboxSkipAndAddsArtifactEntries()
    {
        using var fixture = new RepositoryFixture();
        const string seed = "# .mcg-sandbox handled by operator\n";
        fixture.SeedExclude(seed);

        Assert.True(OrchestratorWorktreeGitExclude.Apply(fixture.Linked));
        Assert.True(OrchestratorWorktreeGitExclude.Apply(fixture.Linked));

        Assert.Equal(seed + string.Join('\n', OrchestratorWorktreeGitExclude.DispatchArtifactEntries) + "\n",
            File.ReadAllText(fixture.ExcludePath));
        Assert.DoesNotContain(File.ReadAllLines(fixture.ExcludePath), line => line.Trim() == ".mcg-sandbox/");
        foreach (var entry in OrchestratorWorktreeGitExclude.DispatchArtifactEntries)
            AssertEntryOnce(fixture.ExcludePath, entry);
    }

    [Theory]
    [InlineData(".mcg-sandbox-prep-receipt-v1.json")]
    [InlineData("/.mcg-sandbox-prep-receipt-v1.json")]
    public void Apply_ReceiptNameAlone_DoesNotSuppressSandboxEntry(string receiptEntry)
    {
        using var fixture = new RepositoryFixture();
        fixture.SeedExclude("  " + receiptEntry + "  \r\n");

        Assert.True(OrchestratorWorktreeGitExclude.Apply(fixture.Linked));
        Assert.True(OrchestratorWorktreeGitExclude.Apply(fixture.Linked));

        AssertEntryOnce(fixture.ExcludePath, ".mcg-sandbox/");
        foreach (var entry in OrchestratorWorktreeGitExclude.DispatchArtifactEntries)
            AssertEntryOnce(fixture.ExcludePath, entry);
    }

    [Fact]
    public void Entries_DispatchArtifacts_StayRootAnchoredAndMatchInternalPathNames()
    {
        var entries = OrchestratorWorktreeGitExclude.DispatchArtifactEntries;
        Assert.Equal(3, entries.Count);
        Assert.All(entries, entry => Assert.StartsWith("/", entry));
        var names = entries.Select(entry => entry.TrimStart('/')).ToArray();
        Assert.Equal(new[] { WorkerSandboxPreparer.MarkerFileName, WorkerSandboxPreparer.ReceiptFileName,
            OrchestratorWorktreeGitExclude.HandoffFileName }, names);
        Assert.All(names, name => Assert.True(GitCli.IsOrchestratorInternalArtifactPath(name)));
    }

    private static void AssertEntryOnce(string path, string entry) =>
        Assert.Single(File.ReadAllLines(path).Where(line => line.Trim() == entry));

    private sealed class RepositoryFixture : IDisposable
    {
        public string Root { get; } = SharedTestSupport.CreateTempDirectory();
        public string Target => Path.Combine(Root, "target");
        public string Linked => Path.Combine(Root, "linked");
        public string ExcludePath => ResolveExclude(Linked);

        public RepositoryFixture()
        {
            try
            {
                var available = GitCli.Run(Root, "--version");
                if (!available.Succeeded)
                    Assert.Skip("Real git is unavailable: " + available.Error);
                Directory.CreateDirectory(Target);
                RunGit(Target, "-c", "init.templateDir=", "init");
                var emptyExcludes = Path.Combine(Root, "empty-excludes");
                File.WriteAllText(emptyExcludes, string.Empty);
                RunGit(Target, "config", "core.excludesFile", emptyExcludes);
                RunGit(Target, "config", "core.hooksPath", Path.Combine(Root, "no-hooks"));
                RunGit(Target, "config", "core.autocrlf", "false");
                Directory.CreateDirectory(Path.Combine(Target, "src"));
                File.WriteAllText(Path.Combine(Target, "src", "App.cs"), "class App { }\n");
                RunGit(Target, "add", "src/App.cs");
                RunGit(Target, "-c", "user.name=Test", "-c", "user.email=test@example.invalid",
                    "-c", "commit.gpgsign=false", "commit", "-m", "fixture");
                RunGit(Target, "worktree", "add", "-b", "linked", Linked);
                Assert.False(File.Exists(Path.Combine(Target, ".gitignore")));
                Assert.False(File.Exists(Path.Combine(Linked, ".gitignore")));
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public string Git(params string[] args) => RunGit(Linked, args);

        public void Prepare() => DispatchProcessHost.PrepareSharedSandboxState(
            new DispatchProcessHost.DispatchRunParameters(
                "unused", Linked, "unused.out", "unused.err", "unused.exit", null, false));

        public string Digest()
        {
            Assert.True(WorktreeTreeDigest.TryCompute(Linked, out var digest, out var reason), reason);
            return digest;
        }

        public void WriteRootArtifacts(string content)
        {
            File.WriteAllText(Path.Combine(Linked, WorkerSandboxPreparer.MarkerFileName), content);
            File.WriteAllText(Path.Combine(Linked, WorkerSandboxPreparer.ReceiptFileName), content);
            File.WriteAllText(Path.Combine(Linked, OrchestratorWorktreeGitExclude.HandoffFileName), content);
        }

        public string WriteReceipt(string digest)
        {
            var path = Path.Combine(Root, WorkerBuildReceipt.FileName);
            File.WriteAllText(path, JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                treeDigest = digest,
                digestAlgorithm = WorktreeTreeDigest.Algorithm,
                buildOutcome = "success",
                generatedAtUtc = "2026-10-19T00:00:00Z",
                worktreeRoot = Linked,
                configuration = "Debug",
                projects = Array.Empty<string>()
            }));
            return path;
        }

        public void SeedExclude(string content)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ExcludePath)!);
            File.WriteAllText(ExcludePath, content);
        }

        private static string RunGit(string target, params string[] args)
        {
            var result = GitCli.Run(target, args);
            Assert.True(result.Succeeded, $"git {string.Join(' ', args)}: {result.Error}");
            return result.Output;
        }

        internal static string ResolveExclude(string target)
        {
            var path = RunGit(target, "rev-parse", "--git-path", "info/exclude").Trim();
            return Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(target, path));
        }

        public void Dispose() => SharedTestSupport.RemoveTempDirectory(Root);
    }
}
