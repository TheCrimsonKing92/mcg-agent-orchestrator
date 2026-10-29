using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: every case owns its repository and pins git identity per child process.
public sealed class ConductorOnlyCandidateRangeTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void MergeAndRetighten_PreservesGoalPatch(bool hermetic, bool retighten)
    {
        using var repo = new Repository();
        repo.Merge(hermetic ? "MCG Acceptance Gate" : "mcg-orchestrator",
            hermetic ? "acceptance-gate@localhost" : "mcg-orchestrator@localhost");
        if (retighten) repo.Ratchet(19, hermetic);

        Assert.True(repo.Eligible());
        Assert.True(ConductorOnlyCandidateRange.IsConductorOnly(repo.Root, "main", repo.Reviewed, repo.Reviewed));
    }

    [Theory]
    [InlineData("worker", "worker@example.invalid")]
    [InlineData("mcg-orchestrator", "worker@example.invalid")]
    [InlineData("worker", "mcg-orchestrator@localhost")]
    [InlineData("MCG Acceptance Gate", "worker@example.invalid")]
    public void WorkerCommitter_RejectsConductorAuthorAndSubject(string name, string email)
    {
        using var repo = new Repository();
        repo.Merge(name, email, "Integrate main into goal before acceptance");

        Assert.False(repo.Eligible());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ConductorCommittedGoalWork_RejectsChangedPatch(bool hermetic)
    {
        using var repo = new Repository();
        repo.Merge();
        repo.Write("goal.txt", "unreviewed Developer work\n");
        Commit(repo.Root, hermetic ? "MCG Acceptance Gate" : "mcg-orchestrator",
            hermetic ? "acceptance-gate@localhost" : "mcg-orchestrator@localhost", "Retighten source size ratchet");

        Assert.False(repo.Eligible());
    }

    [Theory]
    [InlineData("increase")]
    [InlineData("text")]
    [InlineData("delete")]
    public void RatchetException_RejectsOtherChanges(string change)
    {
        using var repo = new Repository();
        repo.Merge();
        if (change == "increase") repo.WriteAuthority(21);
        if (change == "text") repo.Write(SourceSizeRatchet.SourcePath, "arbitrary authority rewrite\n");
        if (change == "delete") File.Delete(Path.Combine(repo.Root, SourceSizeRatchet.SourcePath));
        Commit(repo.Root);

        Assert.False(repo.Eligible());
    }

    [Fact]
    public void BinaryGoalPatch_MustRemainEqual()
    {
        using var repo = new Repository();
        File.WriteAllBytes(Path.Combine(repo.Root, "goal.bin"), [0, 1, 2, 3]);
        Commit(repo.Root, "worker", "worker@example.invalid");
        var reviewed = repo.Head;
        repo.Merge();
        Assert.True(ConductorOnlyCandidateRange.IsConductorOnly(repo.Root, "main", reviewed, repo.Head));
        File.WriteAllBytes(Path.Combine(repo.Root, "goal.bin"), [0, 1, 2, 4]);
        Commit(repo.Root);
        Assert.False(ConductorOnlyCandidateRange.IsConductorOnly(repo.Root, "main", reviewed, repo.Head));
    }

    [Fact]
    public void ReviewedOnSecondParent_IsRejected()
    {
        using var repo = new Repository();
        repo.Git("checkout", "-b", "other", "main");
        repo.Write("other.txt", "other first-parent work\n");
        Commit(repo.Root);
        RunAs(repo.Root, "mcg-orchestrator", "mcg-orchestrator@localhost",
            "merge", "--no-ff", "goal", "-m", "Conductor merge");
        Assert.False(repo.Eligible());
    }

    [Theory]
    [InlineData("missing", "HEAD", "main")]
    [InlineData("HEAD", "missing", "main")]
    [InlineData("HEAD", "HEAD", "missing")]
    public void UnresolvableRevision_IsRejected(string reviewed, string landing, string integration)
    {
        using var repo = new Repository();
        Assert.False(ConductorOnlyCandidateRange.IsConductorOnly(repo.Root, integration, reviewed, landing));
    }

    [Fact]
    public void AmbiguousMergeBases_AreRejected()
    {
        using var repo = new Repository();
        var first = repo.Reviewed;
        var second = repo.Git("rev-parse", "main");
        // Two merges with the same independent parents form a criss-cross ancestry.
        var tree = repo.Git("rev-parse", "HEAD^{tree}");
        var candidate = RunAs(repo.Root, "mcg-orchestrator", "mcg-orchestrator@localhost",
            "commit-tree", tree, "-p", first, "-p", second, "-m", "candidate");
        var integration = RunAs(repo.Root, "mcg-orchestrator", "mcg-orchestrator@localhost",
            "commit-tree", tree, "-p", second, "-p", first, "-m", "integration");
        repo.Git("update-ref", "refs/heads/main", integration);
        Assert.Equal(2, repo.Git("merge-base", "--all", candidate, integration).Split('\n').Length);
        Assert.False(ConductorOnlyCandidateRange.IsConductorOnly(repo.Root, "main", candidate, candidate));
    }

    internal static string Commit(string root, string name = "mcg-orchestrator",
        string email = "mcg-orchestrator@localhost", string subject = "Unclassified subject")
    {
        RunAs(root, name, email, "add", "-A");
        RunAs(root, name, email, "commit", "-m", subject);
        return RunAs(root, name, email, "rev-parse", "HEAD");
    }

    internal static string RunAs(string root, string name, string email, params string[] args)
    {
        var result = GitCli.RunWithEnvironment(root, new Dictionary<string, string>
        {
            ["GIT_COMMITTER_NAME"] = name, ["GIT_COMMITTER_EMAIL"] = email,
            ["GIT_AUTHOR_NAME"] = "mcg-orchestrator", ["GIT_AUTHOR_EMAIL"] = "mcg-orchestrator@localhost"
        }, args);
        Assert.True(result.Succeeded && result.ProcessStarted && !result.DrainTimedOut, result.Error);
        return result.Output.Trim();
    }

    internal sealed class Repository : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "mcg-conductor-range", Guid.NewGuid().ToString("N"));
        public string Reviewed { get; }
        public string Head => Git("rev-parse", "HEAD");

        public Repository()
        {
            Directory.CreateDirectory(Root);
            try
            {
                Git("init", "-b", "main");
                Git("config", "core.autocrlf", "false");
                WriteAuthority(20);
                Write("goal.txt", "base\n");
                Commit(Root);
                Git("checkout", "-b", "goal");
                Write("goal.txt", "reviewed goal work\n");
                Reviewed = Commit(Root, "worker", "worker@example.invalid");
                Git("checkout", "main");
                Write("upstream.txt", "upstream advancement\n");
                Commit(Root, "upstream", "upstream@example.invalid");
                Git("checkout", "goal");
            }
            catch { Dispose(); throw; }
        }

        public string Git(params string[] args) => RunAs(Root, "mcg-orchestrator", "mcg-orchestrator@localhost", args);
        public void Merge(string name = "mcg-orchestrator", string email = "mcg-orchestrator@localhost",
            string subject = "Subject is irrelevant") => RunAs(Root, name, email, "merge", "--no-ff", "main", "-m", subject);
        public bool Eligible() => ConductorOnlyCandidateRange.IsConductorOnly(Root, "main", Reviewed, Head);
        public void Ratchet(int ceiling, bool hermetic = false)
        {
            WriteAuthority(ceiling);
            Commit(Root, hermetic ? "MCG Acceptance Gate" : "mcg-orchestrator",
                hermetic ? "acceptance-gate@localhost" : "mcg-orchestrator@localhost");
        }
        public void WriteAuthority(int ceiling) => Write(SourceSizeRatchet.SourcePath,
            $"// authority\nnew SourceSizeCeiling(\"src/guarded.cs\", {ceiling}), // row\n");
        public void Write(string path, string text)
        {
            var absolute = Path.Combine(Root, path);
            Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
            File.WriteAllText(absolute, text);
        }
        public void Dispose() => LandingExecutorTests.TryDeleteDirectory(Root);
    }
}
