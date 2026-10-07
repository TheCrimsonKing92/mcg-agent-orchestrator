using Mcg.AgentOrchestrator.App.Orchestration;

[Xunit.Collection(TestCollections.PostLandingCanary)]
public sealed class PostLandingCanaryTestsLandingBuildStore
{
    [Xunit.Fact]
    public async Task Canary_uses_completed_store_without_rebuilding()
    {
        using var fixture = new CanaryFixture();
        var seedCalls = 0;
        var seed = new LandingAppBuildStore(fixture.StoreRoot, build: (request, _) =>
        {
            seedCalls++;
            ConductorSelfRelaunchSharedAppPayload.CreatePrivateCopy(request.OutputDirectory);
            return new(0, "", "");
        });
        var completed = seed.GetOrBuild(fixture.Repository, fixture.Sha, TimeSpan.FromMinutes(3));
        Assert.Equal(1, seedCalls);
        var buildCalls = 0;
        var store = new LandingAppBuildStore(fixture.StoreRoot, build: (_, _) =>
        {
            buildCalls++;
            return new(23, "", "must reuse stored App");
        });
        var resolver = new LandingAppBuildStoreCanaryBinaryResolver(store);
        Assert.Equal(Path.Combine(completed, LandingAppBuildStore.AppDllName),
            await resolver.ResolveAsync(fixture.Repository, fixture.Sha, CancellationToken.None));
        var runner = fixture.Runner(resolver);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var outcome = await runner.RunAsync(new PostLandingCanaryRequest(fixture.Sha, ["store-test"]), cancellation.Token);
        Assert.True(outcome.Green, outcome.Detail);
        Assert.True(outcome.ExecutedTestCount > 0);
        Assert.Equal(0, buildCalls);
        Assert.True(File.Exists(Path.Combine(completed, LandingAppBuildStore.CompleteMarkerName)));
    }

    [Xunit.Fact]
    public async Task Store_failure_matches_direct_build_evaluation_failure()
    {
        using var fixture = new CanaryFixture();
        var calls = 0;
        var store = new LandingAppBuildStore(fixture.StoreRoot, build: (_, _) =>
        {
            calls++;
            return new(23, "stdout", "compiler error");
        });
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var runner = fixture.Runner(new LandingAppBuildStoreCanaryBinaryResolver(store));
        var storedFailure = await Assert.ThrowsAsync<PostLandingCanaryEvaluationException>(() =>
            runner.RunAsync(new PostLandingCanaryRequest(fixture.Sha, ["store-test"]), cancellation.Token));
        Assert.Equal(1, calls);
        Assert.Equal("Failed to build freshly landed main binary (exit 23): stdout" + Environment.NewLine + "compiler error",
            storedFailure.Message);
        var directRunner = fixture.Runner(null, "git.exe");
        var directFailure = await Assert.ThrowsAsync<PostLandingCanaryEvaluationException>(() =>
            directRunner.RunAsync(new PostLandingCanaryRequest(fixture.Sha, ["direct-test"]), cancellation.Token));
        Assert.Equal(directFailure.GetType(), storedFailure.GetType());
        Assert.Contains("build freshly landed main binary", directFailure.Message);
        Assert.Empty(Directory.GetDirectories(fixture.StoreRoot));
    }

    private sealed class CanaryFixture : IDisposable
    {
        private readonly string _root = Directory.CreateTempSubdirectory("landing-canary-").FullName;
        internal string Repository { get; }
        internal string StoreRoot { get; }
        internal string Sha { get; }

        internal CanaryFixture()
        {
            Repository = Path.Combine(_root, "repository");
            StoreRoot = Path.Combine(_root, "store");
            var sourceFixture = Path.Combine(InfrastructureTestSupport.FindRepositoryRoot(), "tests", "canary-fixture");
            var fixture = Path.Combine(Repository, "tests", "canary-fixture");
            foreach (var source in Directory.GetFiles(sourceFixture, "*", SearchOption.AllDirectories))
            {
                var destination = Path.Combine(fixture, Path.GetRelativePath(sourceFixture, source));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(source, destination);
            }
            Git("init", "--quiet");
            Git("add", "--all");
            Git("-c", "user.name=Canary Test", "-c", "user.email=canary@localhost", "commit", "--quiet", "-m", "fixture");
            Sha = Git("rev-parse", "HEAD").Trim();
        }

        internal PostLandingCanaryRunner Runner(IPostLandingCanaryApplicationBinaryResolver? resolver, string? dotnet = null) =>
            new(Repository, dotnetPath: dotnet,
                buildCacheRoot: Path.Combine(_root, "build"),
                logDirectory: Path.Combine(_root, "logs"),
                applicationBinaryResolver: resolver);

        private string Git(params string[] arguments)
        {
            var result = InfrastructureTestSupport.RunGitProbe(Repository, ["-C", Repository, .. arguments]);
            InfrastructureTestSupport.RequireCompleteGitOutput(result);
            Assert.True(result.Succeeded, $"git probe failed: {result}");
            return result.StandardOutput;
        }

        public void Dispose()
        {
            foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(_root, recursive: true);
        }
    }
}
