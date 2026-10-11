using System.Reflection;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: plain metadata files and an injected runner, all owned by each fixture.
public sealed class GoalGitRefFingerprintReaderTests
{
    [Xunit.Fact]
    public void UnchangedMetadataHasEqualFingerprintsWithoutInvokingRunner()
    {
        using var fixture = new MetadataFixture();
        var first = fixture.Read();
        Assert.NotNull(first);
        Assert.Equal(first, fixture.Read());
        Assert.Empty(fixture.Commands);
        var method = typeof(GoalGitRefFingerprintReader).GetMethod("TryRead",
            BindingFlags.Static | BindingFlags.NonPublic)!;
        Assert.Equal(new[] { typeof(string), typeof(string) },
            method.GetParameters().Select(parameter => parameter.ParameterType).ToArray());
    }

    [Xunit.Theory]
    [Xunit.InlineData("goal-text")]
    [Xunit.InlineData("goal-add")]
    [Xunit.InlineData("goal-remove")]
    [Xunit.InlineData("packed")]
    [Xunit.InlineData("integration")]
    [Xunit.InlineData("HEAD")]
    [Xunit.InlineData("worktree-add")]
    [Xunit.InlineData("worktree-gitdir")]
    [Xunit.InlineData("HEAD-target")]
    public void EachInputChangeInvalidatesFingerprint(string input)
    {
        using var fixture = new MetadataFixture();
        var before = fixture.Read();
        Assert.NotNull(before);
        fixture.Change(input);
        var after = fixture.Read();
        Assert.NotNull(after);
        Assert.NotEqual(before, after);
        Assert.Empty(fixture.Commands);
    }

    [Xunit.Theory]
    [Xunit.InlineData("packed-refs", false)]
    [Xunit.InlineData("refs/heads/goal", true)]
    [Xunit.InlineData("worktrees", true)]
    [Xunit.InlineData("refs/heads/main", false)]
    [Xunit.InlineData("refs/heads/observed", false)]
    public void AbsentOptionalInputsDifferFromPresentInputs(string path, bool directory)
    {
        using var fixture = new MetadataFixture();
        if (directory)
            Directory.Delete(fixture.PathFor(path), recursive: true);
        else
            File.Delete(fixture.PathFor(path));
        var absent = fixture.Read();
        Assert.NotNull(absent);
        var addedPath = directory ? (path == "worktrees" ? "worktrees/first/gitdir" : path + "/first") : path;
        fixture.Write(addedPath, "first input\n");
        var present = fixture.Read();
        Assert.NotNull(present);
        Assert.NotEqual(absent, present);
    }

    [Xunit.Fact]
    public void MissingOrFileShapedMetadataCannotBeReused()
    {
        using var fixture = new MetadataFixture();
        Directory.Delete(fixture.PathFor(""), recursive: true);
        Assert.Null(fixture.Read());
        File.WriteAllText(fixture.PathFor(""), "gitdir: elsewhere\n");
        Assert.Null(fixture.Read());
    }

    [Xunit.Fact]
    public void MissingHeadCannotBeReused()
    {
        using var fixture = new MetadataFixture();
        File.Delete(fixture.PathFor("HEAD"));
        Assert.Null(fixture.Read());
    }

    [Xunit.Theory]
    [Xunit.InlineData("HEAD")]
    [Xunit.InlineData("refs/heads/main")]
    [Xunit.InlineData("refs/heads/goal/nested/seed")]
    [Xunit.InlineData("packed-refs")]
    [Xunit.InlineData("worktrees/seed/gitdir")]
    public void ReadFailureCannotBeReused(string path)
    {
        using var fixture = new MetadataFixture();
        using (var held = new FileStream(fixture.PathFor(path), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.Null(fixture.Read());
        }
        Assert.NotNull(fixture.Read());
    }

    [Xunit.Theory]
    [Xunit.InlineData(true)]
    [Xunit.InlineData(false)]
    public void EscapingReferencePathCannotBeReused(bool head)
    {
        using var fixture = new MetadataFixture();
        if (head)
        {
            fixture.Write("HEAD", "ref: ../outside\n");
            Assert.Null(fixture.Read());
        }
        else
        {
            Assert.Null(GoalGitRefFingerprintReader.TryRead(fixture.Root, "../../../outside"));
        }
    }

    [Xunit.Fact]
    public void DetachedAndPackedHeadTargetsRemainReadable()
    {
        using var fixture = new MetadataFixture();
        File.Delete(fixture.PathFor("refs/heads/observed"));
        fixture.Write("packed-refs", $"{fixture.MainSha} refs/heads/observed\n");
        Assert.NotNull(fixture.Read());
        fixture.Write("HEAD", fixture.MainSha + "\n");
        Assert.NotNull(fixture.Read());
    }

    [Xunit.Fact]
    public void TransientRefLockFilesAreIncluded()
    {
        using var fixture = new MetadataFixture();
        var before = fixture.Read();
        fixture.Write("refs/heads/goal/nested/seed.lock", fixture.TipSha + "\n");
        var after = fixture.Read();
        Assert.NotNull(before);
        Assert.NotNull(after);
        Assert.NotEqual(before, after);
    }

    // Shared only by this slice's three test classes; never consults a real repository.
    internal sealed class MetadataFixture : IDisposable
    {
        internal const string TipsCommand = "for-each-ref --format=%(refname:short) %(objectname) refs/heads/goal/";
        internal const string MergedCommand = "for-each-ref --format=%(refname:short) --merged HEAD refs/heads/goal/";
        internal const string WorktreeCommand = "worktree list --porcelain";
        internal const string MainCommand = "rev-parse --verify refs/heads/main";
        internal static readonly string[] IndexCommands = [TipsCommand, MergedCommand, WorktreeCommand, MainCommand];
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), $"mcg-git-facts-{Guid.NewGuid():N}");
        internal AgentOrchestratorKernel Kernel { get; }
        internal Goal Goal => Kernel.GetGoal(GoalId);
        internal GoalId GoalId { get; }
        internal string Branch => GoalWorktrees.BranchName(GoalId);
        internal string MainSha { get; set; } = new('1', 40);
        internal string TipSha { get; private set; } = new('2', 40);
        internal string MergedOutput { get; set; } = string.Empty;
        internal string WorktreeOutput { get; set; } = string.Empty;
        internal Dictionary<string, string> Tips { get; } = new(StringComparer.Ordinal);
        internal List<string> Commands { get; } = [];
        internal string? FailedCommand { get; set; }
        internal GitCli.GitResult CherryResult { get; set; } = new(1, string.Empty, "inconclusive");

        internal MetadataFixture()
        {
            Directory.CreateDirectory(Root);
            var kernel = new AgentOrchestratorKernel();
            GoalId = kernel.CreateGoal("Reuse terminal git facts", [new TaskSpec(TaskId.New(), "Work", AgentRole.Developer)]).Id;
            var snapshot = kernel.ExportSnapshot();
            Kernel = AgentOrchestratorKernel.FromSnapshot(snapshot with
            {
                Goals = snapshot.Goals.Select(goal => goal with
                {
                    Status = GoalStatus.Verified,
                    Tasks = goal.Tasks.Select(task => task with { Status = WorkTaskStatus.Completed }).ToArray()
                }).ToArray()
            });
            Tips[Branch] = TipSha;
            Write("HEAD", "ref: refs/heads/observed\n");
            Write("refs/heads/observed", MainSha + "\n");
            Write("refs/heads/main", MainSha + "\n");
            Write($"refs/heads/{Branch}", TipSha + "\n");
            Write("refs/heads/goal/nested/seed", TipSha + "\n");
            Write("packed-refs", "# pack-refs with: peeled\n");
            Write("worktrees/seed/gitdir", Path.Combine(Root, "unrelated", ".git") + "\n");
        }

        internal string PathFor(string path) => Path.Combine(Root, ".git", path.Replace('/', Path.DirectorySeparatorChar));
        internal void Write(string path, string text)
        {
            var target = PathFor(path);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllText(target, text);
        }
        internal string? Read() => GoalGitRefFingerprintReader.TryRead(Root, "main");
        internal GoalGitFactIndex Build(GoalGitFactSnapshotCache? cache, GoalBranchContentStateCache? cherryCache = null) =>
            GoalGitFactIndex.Build(Root, "main", RunGit, cherryCache, cache);

        internal GitCli.GitResult RunGit(string workingDirectory, IReadOnlyList<string> args)
        {
            Assert.Equal(Root, workingDirectory);
            var command = string.Join(" ", args);
            Commands.Add(command);
            if (command == FailedCommand)
                return new(1, string.Empty, "fact command failed");
            return command switch
            {
                TipsCommand => new(0, string.Join("\n", Tips.Select(tip => $"{tip.Key} {tip.Value}")), string.Empty),
                MergedCommand => new(0, MergedOutput, string.Empty),
                WorktreeCommand => new(0, WorktreeOutput, string.Empty),
                _ when args[0] == "rev-parse" => new(0, MainSha + "\n", string.Empty),
                _ when args[0] == "cherry" => CherryResult,
                _ => new(1, string.Empty, $"unexpected command: {command}")
            };
        }

        internal void Change(string input)
        {
            switch (input)
            {
                case "goal-text":
                    TipSha = new('4', 40);
                    Tips[Branch] = TipSha;
                    Write($"refs/heads/{Branch}", TipSha + "\n");
                    break;
                case "goal-add":
                    Tips["goal/nested/added"] = new('4', 40);
                    Write("refs/heads/goal/nested/added", Tips["goal/nested/added"] + "\n");
                    break;
                case "goal-remove":
                    Tips.Remove(Branch);
                    File.Delete(PathFor($"refs/heads/{Branch}"));
                    break;
                case "packed":
                    MainSha = new('5', 40);
                    Write("packed-refs", $"{MainSha} refs/heads/packed-branch\n");
                    break;
                case "integration":
                    MainSha = new('5', 40);
                    Write("refs/heads/main", MainSha + "\n");
                    break;
                case "HEAD":
                    Write("HEAD", new string('6', 40) + "\n");
                    MergedOutput = Branch + "\n";
                    break;
                case "HEAD-target":
                    Write("refs/heads/observed", new string('6', 40) + "\n");
                    MergedOutput = Branch + "\n";
                    break;
                case "worktree-add":
                case "worktree-gitdir":
                    var worktree = GoalWorktrees.WorktreePath(Root, GoalId);
                    Write($"worktrees/{(input == "worktree-add" ? "added" : "seed")}/gitdir", Path.Combine(worktree, ".git") + "\n");
                    WorktreeOutput = $"worktree {worktree.ToUpperInvariant()}\nHEAD {TipSha}\n";
                    break;
                default:
                    throw new ArgumentException("Unknown metadata input", nameof(input));
            }
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
