using System.Text;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Each fixture owns a unique real repository, its worktree and all repository-local configuration.
internal sealed class AdditiveConflictGitFixture : GoalWorktreeTestBase, IDisposable
{
    internal sealed record ConflictFile(string Path, string? Base, string Main, string Goal);

    internal const string WidgetPath = "src/Widget.cs";
    internal const string BaseText = "class Widget\n{\n}\n";
    internal const string MainText = "class Widget\n{\n    void Main() {}\n}\n";
    internal const string GoalText = "class Widget\n{\n    void Goal() {}\n}\n";
    internal const string MergedText = "class Widget\n{\n    void Main() {}\n    void Goal() {}\n}\n";
    private readonly UTF8Encoding encoding;
    private readonly string lineEnding;

    internal string Repository { get; }
    internal string Worktree { get; }
    internal GoalId GoalId { get; }
    internal string OriginalHead { get; }
    internal string MainHead { get; }

    internal AdditiveConflictGitFixture(
        IReadOnlyList<ConflictFile>? files = null, GoalId? goalId = null,
        bool bom = false, string lineEnding = "\n", string? attributes = null)
    {
        Repository = CreateSeededRepository();
        GoalId = goalId ?? Mcg.AgentOrchestrator.Core.GoalId.New();
        encoding = new UTF8Encoding(bom);
        this.lineEnding = lineEnding;
        try
        {
            RunGit(Repository, "config", "core.autocrlf", "false");
            RunGit(Repository, "config", "commit.gpgSign", "false");
            files ??= [new(WidgetPath, BaseText, MainText, GoalText)];
            foreach (var file in files)
                if (file.Base is not null) Write(Repository, file.Path, file.Base);
            if (attributes is not null) Write(Repository, ".gitattributes", attributes);
            Commit(Repository, "Fixture base");
            Worktree = GoalWorktrees.Ensure(Repository, GoalId);
            foreach (var file in files) Write(Worktree, file.Path, file.Goal);
            Commit(Worktree, "Goal changes");
            OriginalHead = Git(Worktree, "rev-parse", "HEAD").Trim();
            foreach (var file in files) Write(Repository, file.Path, file.Main);
            Commit(Repository, "Main changes");
            MainHead = Git(Repository, "rev-parse", "HEAD").Trim();
        }
        catch
        {
            DeleteDirectory(Repository);
            throw;
        }
    }

    internal string Read(string path = WidgetPath) =>
        File.ReadAllText(Path.Combine(Worktree, path)).ReplaceLineEndings("\n");

    internal void AssertRestored()
    {
        Assert.Equal(OriginalHead, Git(Worktree, "rev-parse", "HEAD").Trim());
        Assert.Equal(OriginalHead, Git(Repository, "rev-parse", GoalWorktrees.BranchName(GoalId)).Trim());
        Assert.Equal("", Git(Worktree, "status", "--porcelain=v1", "--untracked-files=all"));
        Assert.Equal(1, GitCli.Run(Worktree, "rev-parse", "--verify", "--quiet", "MERGE_HEAD").ExitCode);
        Assert.Equal(GoalWorktrees.BranchName(GoalId), Git(Worktree, "symbolic-ref", "--short", "HEAD").Trim());
    }

    internal static string Git(string path, params string[] args)
    {
        var result = GitCli.Run(path, args);
        Assert.True(result.Succeeded && !result.DrainTimedOut, $"git {string.Join(' ', args)}: {result.ExitCode}: {result.Error}");
        return result.Output;
    }

    internal static void Commit(string path, string message)
    {
        RunGit(path, "add", "-A");
        RunGit(path, "commit", "-m", message);
    }

    private void Write(string root, string path, string text)
    {
        var fullPath = Path.Combine(root, path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, text.ReplaceLineEndings(lineEnding), encoding);
    }

    public void Dispose() => DeleteDirectory(Repository);
}
