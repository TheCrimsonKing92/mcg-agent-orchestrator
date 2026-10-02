using System.Runtime.CompilerServices;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;

internal sealed class PromptRolloutTestRepository : IDisposable
{
    internal string Root { get; } = Directory.CreateTempSubdirectory("mcg-prompt-rollout-").FullName;
    internal string StorePath => Path.Combine(Root, ".orchestrator", PromptRolloutWatchStore.FileName);
    internal string LandingSha { get; }

    internal PromptRolloutTestRepository()
    {
        RunGit("init", "-b", "main");
        RunGit("config", "user.name", "Prompt rollout test");
        RunGit("config", "user.email", "prompt-rollout@example.invalid");
        RunGit("config", "commit.gpgsign", "false");
        RunGit("config", "core.hooksPath", Path.Combine(Root, "disabled-hooks"));
        var source = File.ReadAllLines(Path.Combine(SourceRoot(), PromptRolloutPhraseStep.RoleRequirementsPath));
        var baseline = string.Join('\n', source.Where(line =>
            !line.Contains("negative_control", StringComparison.Ordinal) && !line.Contains("revert-src", StringComparison.Ordinal)));
        Assert.Contains("evidence_request", baseline);
        Write(PromptRolloutPhraseStep.RoleRequirementsPath, baseline + "\n");
        Commit("first parent");
        File.AppendAllText(Path.Combine(Root, PromptRolloutPhraseStep.RoleRequirementsPath),
            """
            const string control = "evidence_request negative_control:\"revert-src\"";
            """ + "\n");
        LandingSha = Commit("prompt landing");
    }

    internal void Write(string path, string content)
    {
        var fullPath = Path.Combine(Root, path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
    }

    internal string Commit(string message)
    {
        RunGit("add", ".");
        RunGit("commit", "-m", message);
        return RunGit("rev-parse", "HEAD").Trim();
    }

    internal string RunGit(params string[] args)
    {
        var result = GitCli.Run(Root, args);
        Assert.True(result.Succeeded && !result.DrainTimedOut,
            $"git {string.Join(' ', args)} failed ({result.ExitCode}): {result.Error}");
        return result.Output;
    }

    private static string SourceRoot([CallerFilePath] string sourceFile = "")
    {
        if (VerifiedRepositoryRoot.TryGetVerifiedRoot(out var root)) return root;
        var directory = new DirectoryInfo(Path.GetDirectoryName(sourceFile)!);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Mcg.AgentOrchestrator.sln"))) return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Prompt rollout fixture source repository was not found.");
    }

    public void Dispose()
    {
        foreach (var path in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
            File.SetAttributes(path, FileAttributes.Normal);
        Directory.Delete(Root, recursive: true);
    }
}
