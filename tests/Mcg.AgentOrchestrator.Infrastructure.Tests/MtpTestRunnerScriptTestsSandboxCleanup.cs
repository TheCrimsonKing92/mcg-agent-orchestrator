using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection(TestCollections.ProcessSpawning)]
public sealed class MtpTestRunnerScriptTestsSandboxCleanup
{
    [Fact]
    public void DisposeRemovesBothSandboxRootsFromTheRepositoryRoot()
    {
        using var sandbox = MtpTestRunnerScriptTests.ScriptSandbox.Create("success");
        var root = sandbox.Root;
        var localApplicationDataRoot = sandbox.LocalApplicationDataRoot;

        sandbox.Dispose();

        Assert.False(Directory.Exists(root), $"Sandbox root still exists: {root}");
        Assert.False(
            Directory.Exists(localApplicationDataRoot),
            $"Sandbox LOCALAPPDATA root still exists: {localApplicationDataRoot}");
    }

    [Fact]
    public void CompletedRunLeavesNoSandboxEntryInGitStatus()
    {
        var repositoryRoot = MtpTestRunnerScriptTests.RepositoryRoot();
        var before = ReadGitStatus(repositoryRoot);
        using var sandbox = MtpTestRunnerScriptTests.ScriptSandbox.Create("success");
        var rootLeaf = Path.GetFileName(sandbox.Root);
        var localApplicationDataRootLeaf = Path.GetFileName(sandbox.LocalApplicationDataRoot);

        Assert.DoesNotContain(rootLeaf, before, StringComparison.Ordinal);
        Assert.DoesNotContain(localApplicationDataRootLeaf, before, StringComparison.Ordinal);

        var result = sandbox.RunPartition("GoalWorktree");
        Assert.Equal(0, result.ExitCode);
        sandbox.Dispose();

        var after = ReadGitStatus(repositoryRoot);
        Assert.DoesNotContain(rootLeaf, after, StringComparison.Ordinal);
        Assert.DoesNotContain(localApplicationDataRootLeaf, after, StringComparison.Ordinal);
    }

    [Fact]
    public void SandboxRootsAreIgnoredBeforeCleanup()
    {
        var repositoryRoot = MtpTestRunnerScriptTests.RepositoryRoot();
        using var sandbox = MtpTestRunnerScriptTests.ScriptSandbox.Create("success");
        File.WriteAllText(Path.Combine(sandbox.Root, "untracked-probe.txt"), "probe");

        var status = ReadGitStatus(repositoryRoot);

        Assert.DoesNotContain(Path.GetFileName(sandbox.Root), status, StringComparison.Ordinal);
        Assert.DoesNotContain(Path.GetFileName(sandbox.LocalApplicationDataRoot), status, StringComparison.Ordinal);
    }

    [Fact]
    public void UndeletableRootThrowsDiagnosticNamingPathAndReason()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), ".mtp-sandbox-undel"));

        var exception = Assert.Throws<IOException>(() => ScriptSandboxCleanup.DeleteOrThrow(
            [root],
            _ => true,
            _ => throw new IOException("simulated lock owner"),
            attempts: 3));

        Assert.Contains(root, exception.Message, StringComparison.Ordinal);
        Assert.Contains("IOException: simulated lock owner", exception.Message, StringComparison.Ordinal);
        Assert.Contains("after 3 attempts", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Do not commit this directory", exception.Message, StringComparison.Ordinal);
        Assert.Contains("delete the directory", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FirstRootFailureStillAttemptsTheSecondRoot()
    {
        var firstRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), ".mtp-sandbox-first"));
        var secondRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), ".mtp-local-second"));
        HashSet<string> existingRoots = [firstRoot, secondRoot];
        List<string> attemptedRoots = [];

        var exception = Assert.Throws<IOException>(() => ScriptSandboxCleanup.DeleteOrThrow(
            [firstRoot, secondRoot],
            existingRoots.Contains,
            path =>
            {
                attemptedRoots.Add(path);
                if (path == firstRoot)
                {
                    throw new IOException("simulated first-root lock");
                }

                existingRoots.Remove(path);
            },
            attempts: 3));

        Assert.Contains(secondRoot, attemptedRoots);
        Assert.DoesNotContain(secondRoot, existingRoots);
        Assert.Contains(firstRoot, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(secondRoot, exception.Message, StringComparison.Ordinal);
    }

    private static string ReadGitStatus(string repositoryRoot)
    {
        var result = GitCli.Run(repositoryRoot, "status", "--porcelain", "--untracked-files=all");
        Assert.True(
            result.Succeeded,
            $"git status --porcelain --untracked-files=all failed ({result.ExitCode}): " +
            $"{result.Output}{Environment.NewLine}{result.Error}");
        return result.Output;
    }
}
