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
            File.AppendAllText(Path.Combine(Repository, ".git", "config"),
                "\n[core]\n\tautocrlf = false\n[commit]\n\tgpgSign = false\n");
            files ??= [new(WidgetPath, BaseText, MainText, GoalText)];
            var basePaths = new List<string>();
            foreach (var file in files)
            {
                if (file.Base is null) continue;
                Write(Repository, file.Path, file.Base);
                basePaths.Add(file.Path);
            }
            if (attributes is not null)
            {
                Write(Repository, ".gitattributes", attributes);
                basePaths.Add(".gitattributes");
            }
            if (basePaths.Count > 0)
                RunGit(Repository, ["add", "-f", "--", .. basePaths]);
            // An add/add fixture intentionally has no file at the base commit.
            RunGit(Repository, "commit", "--allow-empty", "-m", "Fixture base");
            Worktree = GoalWorktrees.Ensure(Repository, GoalId);
            var paths = files.Select(file => file.Path).ToArray();
            foreach (var file in files) Write(Worktree, file.Path, file.Goal);
            RunGit(Worktree, ["add", "-f", "--", .. paths]);
            RunGit(Worktree, "commit", "-m", "Goal changes");
            OriginalHead = InfrastructureTestSupport.TryReadGitHeadInProcess(Worktree, out var originalHead) && originalHead is not null
                ? originalHead
                : Git(Worktree, "rev-parse", "HEAD").Trim();
            foreach (var file in files) Write(Repository, file.Path, file.Main);
            RunGit(Repository, ["add", "-f", "--", .. paths]);
            RunGit(Repository, "commit", "-m", "Main changes");
            MainHead = InfrastructureTestSupport.TryReadGitHeadInProcess(Repository, out var mainHead) && mainHead is not null
                ? mainHead
                : Git(Repository, "rev-parse", "HEAD").Trim();
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

    internal static string RatchetRegistry(int lines, int files, string comment) =>
        RegistryText(FileRow(WidgetPath, lines, comment), ClassRow(lines, files, comment));

    internal static string RegistryText(string rows, string classes) =>
        "internal static class SourceSizeRatchet\n{\n" +
        "    internal static SourceSizeCeiling[] SeededCeilings = new[]\n    {\n" + rows + "    };\n" +
        "    internal static SourceClassCeiling[] SeededClassCeilings = new[]\n    {\n" + classes + "    };\n}\n";

    internal static string FileRow(string path, int lines, string comment) =>
        $"        // {comment}\n        new SourceSizeCeiling(\"{path}\", {lines}),\n";

    internal static string ClassRow(int lines, int files, string comment) =>
        $"        // {comment}\n        new SourceClassCeiling(\"Widget\", {lines}, {files}),\n";

    internal static IReadOnlyList<ConflictFile> RatchetFiles(bool extraConflict = false, bool absentClass = false)
    {
        const string part = "partial class Widget\n{\n}\n";
        var files = new List<ConflictFile>
        {
            new(SourceSizeRatchet.SourcePath, RatchetRegistry(10, 1, "base"),
                RatchetRegistry(11, 1, "goal A"), RatchetRegistry(12, 2, "goal B")),
            new(WidgetPath, "// widget\n", "// widget\n", "// widget\n")
        };
        if (!absentClass) files.Add(new("src/WidgetPart.cs", part, part, part));
        if (extraConflict) files.Add(new("src/Other.cs", "base\n", "main\n", "goal\n"));
        return files;
    }

    private void Write(string root, string path, string text)
    {
        var fullPath = Path.Combine(root, path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, text.ReplaceLineEndings(lineEnding), encoding);
    }

    public void Dispose() => DeleteDirectory(Repository);
}
