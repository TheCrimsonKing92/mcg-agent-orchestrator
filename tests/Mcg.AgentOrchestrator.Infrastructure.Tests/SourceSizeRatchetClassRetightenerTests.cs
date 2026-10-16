using System.Text;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each git fixture owns its repository; the progress override is AsyncLocal.
public sealed class SourceSizeRatchetClassRetightenerTests : GoalWorktreeTestBase
{
    [Fact]
    public void RuleA_LoweredRowWithNoAddedLines_ChangesOnlyItsTotal()
    {
        var authority = "\uFEFF// justification\r\nnew SourceSizeCeiling(\"src/X.cs\", 12),\r\n" +
            "new SourceClassCeiling(\"C\", 90, 2), // keep\r\n" +
            "new SourceClassCeiling(\"D\", 40, 1),\r\n";
        var inputs = new SourceSizeRatchetRetightener.ClassRaiseInputs(_ => 0, _ => (101, 2), []);
        var actual = Rewrite(authority, Row(100), (91, 2), inputs, out var rows);
        Assert.Equal(Encoding.UTF8.GetBytes(authority.Replace("90, 2", "91, 2", StringComparison.Ordinal)),
            Encoding.UTF8.GetBytes(actual));
        Assert.Equal(new SourceSizeRetightenedRow("class:C", 90, 91, Rule: "A"), Assert.Single(rows));
    }

    [Fact]
    public void RuleB_GrowthWithinBranchCeiling_RaisesToIntegratedTotal()
    {
        var actual = Rewrite(Row(95), Row(95), (100, 2), RuleB(), out var rows);
        Assert.Equal(Row(100), actual);
        Assert.Equal(new SourceSizeRetightenedRow("class:C", 95, 100, Rule: "B"), Assert.Single(rows));
    }

    [Theory]
    [InlineData("head-overflow")]
    [InlineData("unexplained")]
    [InlineData("duplicate")]
    [InlineData("added-lines")]
    [InlineData("line-comment")]
    [InlineData("block-comment")]
    [InlineData("string")]
    [InlineData("verbatim-string")]
    [InlineData("raw-string")]
    [InlineData("duplicate-main")]
    [InlineData("duplicate-head")]
    [InlineData("comment-main")]
    [InlineData("comment-head")]
    [InlineData("missing-base")]
    [InlineData("missing-head")]
    [InlineData("missing-main-measurement")]
    [InlineData("branch-raised-row")]
    [InlineData("added-partial")]
    public void IneligibleRaises_LeaveAuthorityByteIdentical(string scenario)
    {
        var authority = Row(95);
        var main = Row(95);
        var measured = (TotalLines: 100, PartialFiles: 2);
        var inputs = RuleB();
        switch (scenario)
        {
            case "head-overflow": inputs = RuleB(head: (121, 2), basis: (116, 2)); break;
            case "unexplained": measured = (101, 2); break;
            case "duplicate": authority += authority; break;
            case "added-lines":
                authority = Row(90); main = Row(100); measured = (91, 2);
                inputs = new(_ => 1, _ => (101, 2), []); break;
            case "line-comment": authority = "// " + authority; break;
            case "block-comment": authority = "/* " + authority + " */"; break;
            case "string": authority = "var s = \"" + authority.Replace("\"", "\\\"").TrimEnd() + "\";"; break;
            case "verbatim-string": authority = "var s = @\"" + authority.Replace("\"", "\"\"") + "\";"; break;
            case "raw-string": authority = "var s = \"\"\"\n" + authority + "\"\"\";"; break;
            case "duplicate-main": main += main; break;
            case "duplicate-head": inputs = RuleB(headAuthority: Row(120) + Row(120)); break;
            case "comment-main": main = "// " + main; break;
            case "comment-head": inputs = RuleB(headAuthority: "// " + Row(120)); break;
            case "missing-base": inputs = new(_ => null, _ => (95, 2), [new(Row(120), _ => (110, 2), _ => null)]); break;
            case "missing-head": inputs = new(_ => null, _ => (95, 2), [new(Row(120), _ => null, _ => (105, 2))]); break;
            case "missing-main-measurement": inputs = RuleB() with { Main = _ => null }; break;
            case "branch-raised-row": authority = Row(96); break;
            case "added-partial":
                authority = Row(90, 3); main = Row(100, 2); measured = (91, 3);
                inputs = new(_ => 0, _ => (101, 2), []); break;
        }
        var actual = Rewrite(authority, main, measured, inputs, out var rows);
        Assert.Equal(Encoding.UTF8.GetBytes(authority), Encoding.UTF8.GetBytes(actual));
        Assert.Empty(rows);
    }

    [Fact]
    public void RuleB_PartialFileGrowth_RaisesEachFieldIndependently()
    {
        var inputs = RuleB(head: (110, 3), headAuthority: Row(120, 3));
        var actual = Rewrite(Row(95), Row(95), (100, 3), inputs, out var rows);
        Assert.Equal(Row(100, 3), actual);
        Assert.Equal(new[]
        {
            new SourceSizeRetightenedRow("class:C", 95, 100, Rule: "B"),
            new SourceSizeRetightenedRow("class:C", 2, 3, Rule: "B", Field: "files")
        }, rows);

        // An unauthorised count must stay overflowing even when the total raise is justified.
        actual = Rewrite(Row(95), Row(95), (100, 3), inputs with
        {
            Branches = [new(Row(120, 2), _ => (110, 3), _ => (105, 2))]
        }, out rows);
        Assert.Equal(Row(100), actual);
        Assert.Null(Assert.Single(rows).Field);
    }

    [Fact]
    public void RuleA_LoweredFileCount_RaisesOnlyWithinMainCount()
    {
        var actual = Rewrite(Row(100, 1), Row(100, 2), (100, 2),
            new(_ => 0, _ => (101, 2), []), out var rows);
        Assert.Equal(Row(100, 2), actual);
        Assert.Equal(new SourceSizeRetightenedRow("class:C", 1, 2, Rule: "A", Field: "files"), Assert.Single(rows));
    }

    [Fact]
    public void LoweringAndRaisingDifferentFields_PreservesExistingLowering()
    {
        var actual = Rewrite(Row(95, 3), Row(95, 3), (100, 2), RuleB(), out var rows);
        Assert.Equal(Row(100), actual);
        Assert.Equal(new SourceSizeRetightenedRow("class:C", 95, 95, 3, 2), rows[0]);
        Assert.Equal(new SourceSizeRetightenedRow("class:C", 95, 100, Rule: "B"), rows[1]);
        Assert.Equal(2, rows.Count);
    }

    [Theory]
    [InlineData("main-first", false)]
    [InlineData("branch-first", false)]
    [InlineData("rebase", false)]
    [InlineData("main-first", true)]
    public void RetightenAndCommit_RuleB_ProducesOneCommitAndProgress(string topology, bool newPartial)
    {
        var repo = CreateRuleBRepository(topology, newPartial);
        var progress = new List<string>();
        var previous = SourceSizeRatchetRetightener.ProgressSink;
        try
        {
            var integratedHead = RunGitOutput(repo, "rev-parse", "HEAD").Trim();
            Assert.Equal(Row(95), File.ReadAllText(AuthorityPath(repo)));
            var measurement = SourceSizeRatchet.MeasureClasses(repo, ["C"], out var unreadable);
            Assert.Empty(unreadable);
            Assert.Equal((100, newPartial ? 3 : 2), measurement["C"]);
            SourceSizeRatchetRetightener.ProgressSink = progress.Add;
            var rows = SourceSizeRatchetRetightener.RetightenAndCommit(repo, "main", "11111111");
            Assert.Equal(newPartial ? 2 : 1, rows.Count);
            Assert.Equal(Row(100, newPartial ? 3 : 2), File.ReadAllText(AuthorityPath(repo)));
            Assert.Equal("1", RunGitOutput(repo, "rev-list", "--count", $"{integratedHead}..HEAD").Trim());
            Assert.Equal(integratedHead, RunGitOutput(repo, "rev-parse", "HEAD^").Trim());
            Assert.Equal(SourceSizeRatchetRetightener.CommitSubject, RunGitOutput(repo, "log", "-1", "--format=%s").Trim());
            var body = RunGitOutput(repo, "log", "-1", "--format=%b");
            Assert.Contains("class C: 95 -> 100 (rule B)", body);
            var expected = new List<string> { "RATCHET_RETIGHTEN goal=11111111 class=C old=95 new=100 rule=B" };
            if (newPartial)
            {
                Assert.Contains("class C files: 2 -> 3 (rule B)", body);
                expected.Add("RATCHET_RETIGHTEN goal=11111111 class=C field=files old=2 new=3 rule=B");
            }
            Assert.Equal(expected, progress);
            Assert.Empty(SourceSizeRatchetRetightener.RetightenAndCommit(repo, "main", "11111111"));
            Assert.Equal(string.Empty, RunGitOutput(repo, "status", "--porcelain").Trim());
        }
        finally
        {
            SourceSizeRatchetRetightener.ProgressSink = previous;
            DeleteDirectory(repo);
        }
    }

    [Theory]
    [InlineData("squash")]
    [InlineData("dirty")]
    [InlineData("untracked")]
    public void RetightenAndCommit_AmbiguousOrDirtyEvidence_SkipsRaise(string scenario)
    {
        var repo = CreateRuleBRepository(scenario == "squash" ? "squash" : "main-first", false);
        try
        {
            if (scenario == "dirty") File.AppendAllText(Path.Combine(repo, "src/B.cs"), "// uncommitted\n");
            if (scenario == "untracked") WritePartial(repo, "src/Untracked.cs", 5);
            var authority = File.ReadAllBytes(AuthorityPath(repo));
            var head = RunGitOutput(repo, "rev-parse", "HEAD").Trim();
            Assert.Empty(SourceSizeRatchetRetightener.RetightenAndCommit(repo, "main", "11111111"));
            Assert.Equal(authority, File.ReadAllBytes(AuthorityPath(repo)));
            Assert.Equal(head, RunGitOutput(repo, "rev-parse", "HEAD").Trim());
        }
        finally { DeleteDirectory(repo); }
    }

    private static string Rewrite(string authority, string main, (int TotalLines, int PartialFiles) measured,
        SourceSizeRatchetRetightener.ClassRaiseInputs inputs, out IReadOnlyList<SourceSizeRetightenedRow> rows) =>
        SourceSizeRatchetRetightener.Rewrite(authority, main, _ => 12, _ => null, out rows,
            new Dictionary<string, (int, int)> { ["C"] = measured }, inputs);

    private static SourceSizeRatchetRetightener.ClassRaiseInputs RuleB(
        (int TotalLines, int PartialFiles)? head = null, (int TotalLines, int PartialFiles)? basis = null,
        string? headAuthority = null) => new(_ => throw new InvalidOperationException("Rule B needs no added-lines check."),
            _ => (95, 2), [new(headAuthority ?? Row(120), _ => head ?? (110, 2), _ => basis ?? (105, 2))]);

    private static string Row(int total, int files = 2) => $"new SourceClassCeiling(\"C\", {total}, {files}),\r\n";
    private static string AuthorityPath(string root) => Path.Combine(root, SourceSizeRatchet.SourcePath);

    private static string CreateRuleBRepository(string topology, bool newPartial)
    {
        var repo = CreateSeededRepository();
        try
        {
            RunGit(repo, "branch", "-M", "main");
            Directory.CreateDirectory(Path.GetDirectoryName(AuthorityPath(repo))!);
            WritePartial(repo, "src/A.cs", 60);
            WritePartial(repo, "src/B.cs", 45);
            File.WriteAllText(AuthorityPath(repo), Row(120, newPartial ? 3 : 2), new UTF8Encoding(false));
            RunGit(repo, "add", "src");
            RunGit(repo, "commit", "-m", "Base C totals 105 under 120");
            RunGit(repo, "checkout", "-b", "branch");
            if (newPartial) WritePartial(repo, "src/New.cs", 5);
            else File.AppendAllLines(Path.Combine(repo, "src/B.cs"), Enumerable.Range(1, 5).Select(i => $"// branch {i}"));
            RunGit(repo, "add", "src");
            RunGit(repo, "commit", "-m", "Branch adds five authorised lines");
            RunGit(repo, "checkout", "main");
            WritePartial(repo, "src/A.cs", 50);
            File.WriteAllText(AuthorityPath(repo), Row(95), new UTF8Encoding(false));
            RunGit(repo, "add", "src");
            RunGit(repo, "commit", "-m", "Main extracts ten lines and pins row");
            if (topology is "branch-first" or "rebase")
            {
                RunGit(repo, "checkout", "branch");
                if (topology == "rebase") RunGit(repo, "rebase", "main");
                else RunGit(repo, "merge", "--no-ff", "--no-edit", "main");
            }
            else
            {
                RunGit(repo, "checkout", "--detach", "main");
                if (topology == "squash")
                {
                    RunGit(repo, "merge", "--squash", "branch");
                    RunGit(repo, "commit", "-m", "Squashed integration");
                }
                else RunGit(repo, "merge", "--no-ff", "--no-edit", "branch");
            }
            return repo;
        }
        catch { DeleteDirectory(repo); throw; }
    }

    private static void WritePartial(string root, string path, int count) => File.WriteAllLines(Path.Combine(root, path),
        new[] { "partial class C { }" }.Concat(Enumerable.Range(1, count - 1).Select(i => $"// line {i}")));
}
