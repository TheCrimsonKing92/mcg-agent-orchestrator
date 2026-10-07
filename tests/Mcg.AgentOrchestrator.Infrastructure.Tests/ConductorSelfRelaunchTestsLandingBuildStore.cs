using Mcg.AgentOrchestrator.App.Orchestration;

[Xunit.Collection(TestCollections.ProcessSpawning)]
public sealed class ConductorSelfRelaunchTestsLandingBuildStore
{
    [Xunit.Fact]
    public void Prepare_twice_reuses_store_without_incrementing_direct_build_count()
    {
        using var fixture = new StagingFixture();
        var calls = 0;
        var store = new LandingAppBuildStore(Path.Combine(fixture.Root, "store"), build: (request, _) =>
        {
            calls++;
            ConductorSelfRelaunchSharedAppPayload.CreatePrivateCopy(request.OutputDirectory);
            return new(0, "", "");
        });
        var options = fixture.Options with { LandingAppBuildStore = store };
        Assert.Same(store, options.Staging.LandingAppBuildStore);
        var before = ConductorSelfRelaunch.AppBuildInvocationCount;
        var expectedHead = Git(options.RepositoryRoot, "rev-parse", "HEAD").Trim();
        for (var i = 0; i < 2; i++)
        {
            var successor = ConductorSelfRelaunch.PrepareSuccessor(options.Staging);
            using var lease = successor.RunDirectoryLease;
            Assert.True(File.Exists(successor.AppDllPath));
            Assert.Equal(expectedHead, successor.RepositoryHead);
            Assert.Equal(expectedHead, File.ReadAllText(successor.AppDllPath + ".git-head").Trim());
            Assert.False(File.Exists(Path.Combine(successor.RunDirectory, LandingAppBuildStore.CompleteMarkerName)));
            Assert.DoesNotContain(".partial-", successor.AppDllPath);
        }
        Assert.Equal(1, calls);
        Assert.Equal(before, ConductorSelfRelaunch.AppBuildInvocationCount);
    }

    [Xunit.Fact]
    public void Store_build_failure_preserves_build_preparation_failure()
    {
        using var fixture = new StagingFixture();
        var calls = 0;
        var options = fixture.Options.Staging with
        {
            LandingAppBuildStore = new LandingAppBuildStore(Path.Combine(fixture.Root, "store"), build: (_, _) =>
            {
                calls++;
                return new(23, "stdout", "compiler error");
            })
        };
        var failure = Assert.Throws<ConductorSelfRelaunchPreparationException>(() =>
            ConductorSelfRelaunch.PrepareSuccessor(options));
        Assert.Equal("build", failure.Phase);
        Assert.Equal("build merged conductor failed exit=23 timedOut=False: compiler error", failure.Message);
        Assert.Equal(1, calls);
        Assert.Empty(Directory.GetDirectories(Path.Combine(fixture.Root, "store")));
    }

    [Xunit.Fact]
    public void Explicit_prebuilt_output_takes_precedence_over_store()
    {
        using var fixture = new StagingFixture();
        var prebuilt = Path.Combine(fixture.Root, "prebuilt");
        ConductorSelfRelaunchSharedAppPayload.CreatePrivateCopy(prebuilt);
        var calls = 0;
        var options = fixture.Options.Staging with
        {
            PrebuiltAppOutputDirectory = prebuilt,
            LandingAppBuildStore = new LandingAppBuildStore(Path.Combine(fixture.Root, "store"), build: (_, _) =>
            {
                calls++;
                return new(23, "", "must not build");
            })
        };
        var before = ConductorSelfRelaunch.AppBuildInvocationCount;
        var successor = ConductorSelfRelaunch.PrepareSuccessor(options);
        using var lease = successor.RunDirectoryLease;
        Assert.True(File.Exists(successor.AppDllPath));
        Assert.Equal(0, calls);
        Assert.Equal(before, ConductorSelfRelaunch.AppBuildInvocationCount);
        Assert.False(Directory.Exists(Path.Combine(fixture.Root, "store")));
    }

    private static string Git(string repository, params string[] arguments)
    {
        var result = InfrastructureTestSupport.RunGitProbe(repository, ["-C", repository, .. arguments]);
        InfrastructureTestSupport.RequireCompleteGitOutput(result);
        Assert.True(result.Succeeded, $"git probe failed: {result}");
        return result.StandardOutput;
    }

    private sealed class StagingFixture : IDisposable
    {
        internal string Root { get; } = Directory.CreateTempSubdirectory("landing-relaunch-").FullName;
        internal ConductorSelfRelaunchOptions Options { get; }

        internal StagingFixture()
        {
            var repository = InfrastructureTestSupport.FindRepositoryRoot();
            var orchestrator = Path.Combine(Root, ".orchestrator");
            Directory.CreateDirectory(orchestrator);
            var state = Path.Combine(orchestrator, "state.db");
            _ = InfrastructureTestSupport.CreateMigratedStateRepository(state).LoadAsync().GetAwaiter().GetResult();
            var isolatedTemp = Path.Combine(Root, "isolated-temp");
            Directory.CreateDirectory(isolatedTemp);
            var resolver = Path.Combine(Root, "resolve-test-run-dir.ps1");
            static string Quote(string value) => value.Replace("'", "''", StringComparison.Ordinal);
            File.WriteAllText(resolver, string.Join(Environment.NewLine,
                "param([string]$Dll)",
                "$ErrorActionPreference = 'Stop'",
                $"$env:TEMP = '{Quote(isolatedTemp)}'",
                $"$env:TMP = '{Quote(isolatedTemp)}'",
                $"& '{Quote(Path.Combine(repository, "scripts", "resolve-run-dir.ps1"))}' -Dll $Dll"));
            var handoff = new ConductLoopHandoffOptions(
                Args: ["conduct", "--loop", "--max-iterations", "1"],
                ExecutionDirectory: Root,
                OrchestratorDirectory: orchestrator,
                LogDirectory: Path.Combine(orchestrator, "logs"),
                RunEventStorePath: Path.Combine(orchestrator, "run-events.db"),
                StopFilePath: Path.Combine(Root, ConductorBatchLoop.StopFileName),
                RenewalCount: 0,
                MaxRenewals: ConductorLoopHandoff.DefaultMaxRenewalsWithoutLanding,
                ReleaseCurrentLease: () => throw new InvalidOperationException("Staging must not initiate a handoff."));
            Options = new ConductorSelfRelaunchOptions(
                RepositoryRoot: repository,
                AppProjectPath: Path.Combine(repository, "src", "Mcg.AgentOrchestrator.App", "Mcg.AgentOrchestrator.App.csproj"),
                AppDllPath: Path.Combine(Root, "build-output", LandingAppBuildStore.AppDllName),
                UpdateHeadMarkerScriptPath: Path.Combine(repository, "scripts", "Update-AppDllGitHeadMarker.ps1"),
                ResolveRunDirectoryScriptPath: resolver,
                StateStorePath: state,
                AgentCatalogPath: Path.Combine(orchestrator, "agents.json"),
                WorkerProfilePath: Path.Combine(orchestrator, "worker-profiles.json"),
                ModelFunctionCatalogPath: Path.Combine(orchestrator, "model-functions.json"),
                DotnetPath: Environment.GetEnvironmentVariable("MCG_ORCHESTRATOR_DOTNET_PATH") ?? "dotnet",
                PowerShellPath: "powershell",
                HandoffOptions: handoff,
                BuildTimeout: TimeSpan.FromMinutes(3),
                SelfCheckTimeout: TimeSpan.FromSeconds(60));
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
