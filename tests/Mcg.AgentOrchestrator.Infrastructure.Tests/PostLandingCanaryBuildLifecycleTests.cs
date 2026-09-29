using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Runtime.CompilerServices;
using Mcg.AgentOrchestrator.App.Orchestration;

[Xunit.Collection(TestCollections.PostLandingCanary)]
public sealed class PostLandingCanaryBuildLifecycleTests
{
    [Xunit.Theory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public async Task Run_removes_its_owned_build_output_after_pass_or_probe_failure(bool failProbe)
    {
        var root = Path.Combine(Path.GetTempPath(), $"canary-build-pass-{Guid.NewGuid():N}");
        var repository = Path.Combine(root, "repository");
        var sourceFixture = Path.Combine(RepositoryRoot(), "tests", "canary-fixture");
        var fixture = Path.Combine(repository, "tests", "canary-fixture");
        Directory.CreateDirectory(fixture);
        foreach (var source in Directory.GetFiles(sourceFixture, "*", SearchOption.AllDirectories))
        {
            var destination = Path.Combine(fixture, Path.GetRelativePath(sourceFixture, source));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(source, destination);
        }

        try
        {
            Git(repository, "init", "--quiet");
            Git(repository, "add", "--all");
            Git(repository, "-c", "user.name=Canary Test", "-c", "user.email=canary@localhost",
                "commit", "--quiet", "-m", "fixture");
            var sha = Git(repository, "rev-parse", "HEAD").Trim();
            var buildRoot = Path.Combine(root, "build");
            var repositoryKey = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(repository))))[..16];
            var resolver = new RecordingBinaryResolver(buildRoot, repositoryKey,
                typeof(PostLandingCanaryRunner).Assembly.Location);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            var runner = new PostLandingCanaryRunner(
                repository,
                dotnetPath: failProbe ? "git.exe" : null,
                buildCacheRoot: buildRoot,
                logDirectory: Path.Combine(root, "logs"),
                applicationBinaryResolver: resolver);

            if (failProbe)
            {
                var failure = await Assert.ThrowsAsync<PostLandingCanaryEvaluationException>(() =>
                    runner.RunAsync(new PostLandingCanaryRequest(sha, ["lifecycle-test"]), timeout.Token));
                Assert.Contains("without a result contract", failure.Message);
            }
            else
            {
                var outcome = await runner.RunAsync(
                    new PostLandingCanaryRequest(sha, ["lifecycle-test"]), timeout.Token);
                Assert.True(outcome.Green, outcome.Detail);
            }

            Assert.True(resolver.WroteIntoOwnedOutput);
            Assert.False(Directory.Exists(buildRoot));
        }
        finally
        {
            DeleteTestRoot(root);
        }
    }

    [Xunit.Fact]
    public async Task Failed_build_removes_its_output_and_does_not_reuse_a_matching_legacy_entry()
    {
        var root = Path.Combine(Path.GetTempPath(), $"canary-build-lifecycle-{Guid.NewGuid():N}");
        var repository = Path.Combine(root, "repository");
        var fixture = Path.Combine(repository, "tests", "canary-fixture");
        Directory.CreateDirectory(fixture);
        File.WriteAllText(Path.Combine(fixture, "fixture.txt"), "known green fixture");
        try
        {
            Git(repository, "init", "--quiet");
            Git(repository, "add", "--all");
            Git(repository, "-c", "user.name=Canary Test", "-c", "user.email=canary@localhost",
                "commit", "--quiet", "-m", "fixture");
            var sha = Git(repository, "rev-parse", "HEAD").Trim();
            var buildRoot = Path.Combine(root, "build");
            var repositoryKey = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(repository))))[..16];
            var legacy = Path.Combine(buildRoot, repositoryKey, sha);
            Directory.CreateDirectory(legacy);
            File.WriteAllText(Path.Combine(legacy, "Mcg.AgentOrchestrator.App.dll"), "incomplete cached app");
            File.WriteAllText(Path.Combine(legacy, "Mcg.AgentOrchestrator.App.dll.git-head"), sha);

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            var runner = new PostLandingCanaryRunner(
                repository,
                dotnetPath: "git.exe",
                buildCacheRoot: buildRoot,
                logDirectory: Path.Combine(root, "logs"));
            var failure = await Assert.ThrowsAsync<PostLandingCanaryEvaluationException>(() =>
                runner.RunAsync(new PostLandingCanaryRequest(sha, ["lifecycle-test"]), timeout.Token));

            Assert.Contains("build freshly landed main binary", failure.Message);
            Assert.Equal([legacy], Directory.GetDirectories(Path.Combine(buildRoot, repositoryKey)));
            Assert.Equal("incomplete cached app", File.ReadAllText(Path.Combine(legacy, "Mcg.AgentOrchestrator.App.dll")));
        }
        finally
        {
            DeleteTestRoot(root);
        }
    }

    private static void DeleteTestRoot(string root)
    {
        foreach (var entry in new DirectoryInfo(root).EnumerateFileSystemInfos("*", SearchOption.AllDirectories))
        {
            entry.Attributes &= ~FileAttributes.ReadOnly;
        }
        Directory.Delete(root, recursive: true);
    }

    private static string RepositoryRoot([CallerFilePath] string sourceFile = "")
    {
        for (var path = Path.GetDirectoryName(sourceFile); path is not null; path = Path.GetDirectoryName(path))
        {
            if (Directory.Exists(Path.Combine(path, ".git")) || File.Exists(Path.Combine(path, ".git")))
            {
                return path;
            }
        }
        throw new InvalidOperationException("Repository root not found from test source path.");
    }

    private sealed class RecordingBinaryResolver(string buildRoot, string repositoryKey, string binary)
        : IPostLandingCanaryApplicationBinaryResolver
    {
        public bool WroteIntoOwnedOutput { get; private set; }

        public Task<string> ResolveAsync(string sourceRoot, string sourceSha, CancellationToken cancellationToken)
        {
            var owned = Assert.Single(Directory.GetDirectories(Path.Combine(buildRoot, repositoryKey)));
            File.WriteAllText(Path.Combine(owned, "build-sentinel.txt"), sourceSha);
            WroteIntoOwnedOutput = true;
            return Task.FromResult(binary);
        }
    }

    private static string Git(string repository, params string[] arguments)
    {
        var result = InfrastructureTestSupport.RunGitProbe(repository, ["-C", repository, .. arguments]);
        InfrastructureTestSupport.RequireCompleteGitOutput(result);
        Assert.True(!result.TimedOut, $"git {string.Join(' ', arguments)} timed out: {result}");
        Assert.True(result.Succeeded, $"git {string.Join(' ', arguments)} failed with exit {result.ExitCode}: {result.StandardError}; {result}");
        return result.StandardOutput;
    }
}
