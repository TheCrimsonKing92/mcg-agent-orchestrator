using System.Text;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection(TestCollections.GoalAcceptanceVerifier)]
public sealed class HermeticVerificationEnvironmentTestsGateGitConfig : GoalAcceptanceVerifierTestBase
{
    private const string ExpectedConfig =
        "[core]\n\tautocrlf = true\n\tsymlinks = false\n\tfscache = true\n[init]\n\tdefaultBranch = master\n";

    [Xunit.Fact]
    public void BuilderPinsGitConfigurationWithoutAmbientRepositoryOrGlobalConfig()
    {
        var environment = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["GIT_CONFIG_GLOBAL"] = @"C:\operator\.gitconfig",
            ["GIT_CONFIG_NOSYSTEM"] = "0",
            ["GIT_DIR"] = @"C:\operator\repo\.git"
        };

        GoalAcceptanceVerifier.ConfigureHermeticVerificationEnvironment(environment, Path.GetTempPath());

        Assert.Equal("1", environment["GIT_CONFIG_NOSYSTEM"]);
        Assert.Equal("0", environment["GIT_TERMINAL_PROMPT"]);
        Assert.Equal("never", environment["GCM_INTERACTIVE"]);
        Assert.DoesNotContain("GIT_DIR", environment.Keys);
        var path = Assert.Contains("GIT_CONFIG_GLOBAL", environment)!;
        Assert.Equal(Path.Combine(environment["USERPROFILE"]!, "git", "gate-gitconfig"), path);
        Assert.NotEqual(@"C:\operator\.gitconfig", path);
        Assert.NotEqual(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".gitconfig"), path);
        Assert.Equal(ExpectedConfig, GoalAcceptanceVerifier.HermeticGateGitConfig);
        Assert.Equal(new UTF8Encoding(false).GetBytes(ExpectedConfig), File.ReadAllBytes(path));
        Assert.True(File.GetAttributes(path).HasFlag(FileAttributes.ReadOnly));

        var firstPath = path;
        GoalAcceptanceVerifier.ConfigureHermeticVerificationEnvironment(environment, Path.GetTempPath());
        Assert.Equal(firstPath, environment["GIT_CONFIG_GLOBAL"]);
        Assert.Equal("1", environment["GIT_CONFIG_NOSYSTEM"]);
        Assert.Equal("0", environment["GIT_TERMINAL_PROMPT"]);
        Assert.Equal("never", environment["GCM_INTERACTIVE"]);
    }

    [Xunit.Fact]
    public void WriterRefreshesStaleFileAndConcurrentCallersLeaveOneUnchangedReadOnlyFile()
    {
        var profileRoot = Path.Combine(Path.GetTempPath(), "mcg-git-config-tests", Guid.NewGuid().ToString("N"));
        var directory = Path.Combine(profileRoot, "git");
        var path = Path.Combine(directory, "gate-gitconfig");
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(path, "stale");
            File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.ReadOnly);
            using var gate = new Barrier(2);
            Task<string> StartWriter() => Task.Factory.StartNew(() =>
            {
                gate.SignalAndWait();
                return GoalAcceptanceVerifier.EnsureHermeticGateGitConfig(profileRoot);
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            var first = StartWriter();
            var second = StartWriter();
            Task.WaitAll(first, second);

            Assert.Equal(path, first.Result);
            Assert.Equal(path, second.Result);
            Assert.Equal(new UTF8Encoding(false).GetBytes(ExpectedConfig), File.ReadAllBytes(path));
            Assert.True(File.GetAttributes(path).HasFlag(FileAttributes.ReadOnly));
            Assert.Equal(new[] { path }, Directory.GetFiles(directory));

            var sentinel = new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            File.SetAttributes(path, File.GetAttributes(path) & ~FileAttributes.ReadOnly);
            File.SetLastWriteTimeUtc(path, sentinel);
            File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.ReadOnly);
            Assert.Equal(path, GoalAcceptanceVerifier.EnsureHermeticGateGitConfig(profileRoot));
            Assert.Equal(sentinel, File.GetLastWriteTimeUtc(path));
            Assert.True(File.GetAttributes(path).HasFlag(FileAttributes.ReadOnly));
        }
        finally
        {
            if (File.Exists(path))
            {
                File.SetAttributes(path, File.GetAttributes(path) & ~FileAttributes.ReadOnly);
            }
            Directory.Delete(profileRoot, recursive: true);
        }
    }

    [Xunit.Fact]
    public void ConcurrentBuildersProduceIdenticalGitVariables()
    {
        using var gate = new Barrier(2);
        Task<Dictionary<string, string?>> StartBuilder() => Task.Factory.StartNew(() =>
        {
            var environment = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            gate.SignalAndWait();
            GoalAcceptanceVerifier.ConfigureHermeticVerificationEnvironment(environment, Path.GetTempPath());
            return environment;
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

        var first = StartBuilder();
        var second = StartBuilder();
        Task.WaitAll(first, second);
        foreach (var name in new[] { "GIT_CONFIG_NOSYSTEM", "GIT_CONFIG_GLOBAL", "GIT_TERMINAL_PROMPT", "GCM_INTERACTIVE" })
        {
            Assert.Equal(first.Result[name], second.Result[name]);
        }
        Assert.True(File.GetAttributes(first.Result["GIT_CONFIG_GLOBAL"]!).HasFlag(FileAttributes.ReadOnly));
    }
}
