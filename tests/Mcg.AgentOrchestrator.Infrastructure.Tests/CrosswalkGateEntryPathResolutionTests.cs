using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Mcg.AgentOrchestrator.Infrastructure.Tests;

// Serial: composing the launch temporarily selects the process data root. All repositories,
// registries, child processes and build storage belong exclusively to this fixture.
[Collection(TestCollections.CliProcessEnvironment)]
public sealed class CrosswalkGateEntryPathResolutionTests
{
    [Fact]
    public async Task ProjectScopedSoloAttempt_ResolvesInstalledCrosswalkFromOrchestratorDataRoot()
    {
        using var fixture = new Fixture();
        Directory.CreateDirectory(Path.GetDirectoryName(fixture.Executable)!);
        // Resolution is the contract here. An intentionally non-executable fake makes the
        // subsequent invocation fail, retaining this check in the attempt's unmet criteria.
        File.WriteAllText(fixture.Executable, "fake crosswalk executable for entry-path resolution");
        var expectedHash = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(fixture.Executable)));

        var check = await fixture.RunAsync();

        Assert.Equal(fixture.Executable, check.CrosswalkExecutablePath);
        Assert.Equal(expectedHash, check.CrosswalkExecutableSha256);
        Assert.DoesNotContain(check.CrosswalkDiagnostics!, diagnostic =>
            diagnostic.Category is CrosswalkDiagnosticCategory.NotConfigured or CrosswalkDiagnosticCategory.MissingBinary);
    }

    [Fact]
    public async Task ProjectScopedSoloAttempt_MissingCrosswalk_FailureNamesFullExpectedPath()
    {
        using var fixture = new Fixture();
        Assert.False(File.Exists(fixture.Executable));

        var check = await fixture.RunAsync();

        Assert.False(check.Passed);
        Assert.Null(check.CrosswalkExecutablePath);
        Assert.Null(check.CrosswalkExecutableSha256);
        var diagnostic = Assert.Single(check.CrosswalkDiagnostics!);
        Assert.Equal(CrosswalkDiagnosticCategory.NotConfigured, diagnostic.Category);
        Assert.Contains(fixture.Executable, diagnostic.Detail, StringComparison.Ordinal);
        Assert.Contains(fixture.Executable, check.OutputTail, StringComparison.Ordinal);
    }

    [Fact]
    public void Constructor_WithoutCrosswalkResolver_IsRejectedNamingResolver()
    {
        using var fixture = new Fixture();
        var constructors = typeof(GoalAcceptanceVerifier).GetConstructors();
        Assert.NotEmpty(constructors);
        foreach (var constructor in constructors)
        {
            var parameters = constructor.GetParameters();
            var resolver = Assert.Single(parameters, parameter =>
                parameter.ParameterType == typeof(CrosswalkExecutableResolver));
            Assert.False(resolver.IsOptional);
            var arguments = parameters.Select(parameter => parameter.ParameterType == typeof(CrosswalkExecutableResolver)
                ? null : parameter.ParameterType == typeof(DotnetBuildStorageRoot)
                    ? (object)new DotnetBuildStorageRoot(Path.Combine(fixture.Root, "build")) : fixture.Root).ToArray();
            var invocation = Assert.Throws<TargetInvocationException>(() => constructor.Invoke(arguments));
            var exception = Assert.IsType<ArgumentNullException>(invocation.InnerException);
            Assert.Contains("crosswalkExecutable", exception.ParamName, StringComparison.Ordinal);
            Assert.Contains("crosswalk executable resolver", exception.Message, StringComparison.Ordinal);
        }
        var configured = new GoalAcceptanceVerifier(new CrosswalkExecutableResolver(dataRoot: fixture.Data),
            new DotnetBuildStorageRoot(Path.Combine(fixture.Root, "build")));
        var setter = typeof(GoalAcceptanceVerifier).GetProperty(nameof(GoalAcceptanceVerifier.CrosswalkExecutable))!.SetMethod!;
        var setterInvocation = Assert.Throws<TargetInvocationException>(() => setter.Invoke(configured, [null]));
        Assert.Contains("crosswalk executable resolver",
            Assert.IsType<ArgumentNullException>(setterInvocation.InnerException).Message, StringComparison.Ordinal);
    }

    private sealed class Fixture : IDisposable
    {
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
        internal string Root { get; } = Directory.CreateTempSubdirectory("cw-entry-").FullName;
        internal string Data => Path.Combine(Root, "data");
        internal string Executable => Path.Combine(Data, "tools", "crosswalk", "crosswalk.exe");

        internal async Task<AcceptanceCheckResult> RunAsync()
        {
            var repo = Directory.CreateDirectory(Path.Combine(Root, "repo")).FullName;
            BatchLoopGitRepositoryTemplate.CopyTo(repo);
            Directory.CreateDirectory(Path.Combine(repo, "config"));
            File.WriteAllText(Path.Combine(repo, "config", "acceptance-manifest.json"),
                AcceptanceManifestTestDefaults.WithEngine("""
                { "version": 1, "engine": { "enforceStructuralCoverage": false, "timeouts": {} },
                  "checks": [{ "name": "entry-path crosswalk", "type": "crosswalk-test", "project": ".", "timeoutMinutes": 1 }],
                  "forbiddenChangedPathGlobs": [] }
                """));
            Git(repo, "add", "config/acceptance-manifest.json");
            Git(repo, "commit", "-m", "Seed crosswalk manifest");
            new OrchestratorProjectRegistry(Path.Combine(Data, "projects"), Data).CreateProject("crosswalk-entry", repo);
            var workspace = OrchestratorWorkspace.ForProject("crosswalk-entry", repo, repo, dataRootDirectory: Data);
            Assert.True(workspace.IsProjectScoped);
            var kernel = new AgentOrchestratorKernel();
            var goal = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, AgentCatalog.Default().Agents,
                "Update docs/CrosswalkEntry.md");
            var task = Assert.Single(goal.Tasks);
            kernel.RecordTaskDispatch(goal.Id, task.Id,
                new TaskDispatchRecord("test-worker", "test.exe", repo, DateTimeOffset.UtcNow));
            kernel.RecordTaskVerification(goal.Id, task.Id,
                new TaskVerificationRecord("test.exe", repo, 0, "ok", "", DateTimeOffset.UtcNow));
            var worktree = GoalWorktrees.Ensure(repo, goal.Id);
            Directory.CreateDirectory(Path.Combine(worktree, "docs"));
            File.WriteAllText(Path.Combine(worktree, "docs", "CrosswalkEntry.md"), "entry-path regression");
            Git(worktree, "add", "docs/CrosswalkEntry.md");
            Git(worktree, "commit", "-m", "Add candidate change");
            await InfrastructureTestSupport.CreateMigratedStateRepository(workspace.SqliteStatePath).SaveAsync(kernel);

            var storage = new DotnetBuildStorageRoot(Path.Combine(Root, "build"));
            var coordinator = new ConductorParallelAcceptanceAttemptCoordinator(Path.Combine(Root, "attempts"),
                workspace.IntegrationBranch, executionDirectory: repo, buildStorageRoot: storage,
                stateDirectory: workspace.OrchestratorDirectory, projectName: workspace.ProjectName);
            var candidate = ConductorParallelAcceptanceCandidate.Create(goal, 0, ["docs/CrosswalkEntry.md"]);
            var attempt = coordinator.CreateAttemptForTests(candidate);
            Assert.Equal(workspace.ProjectName, attempt.ProjectName);
            Assert.Equal(workspace.OrchestratorDirectory, attempt.StateDirectory);
            Assert.Equal(1, attempt.HolderContractVersion);
            var holderLease = new AcceptanceHolderLease();
            var binding = AcceptanceHolderBinding.For(attempt);
            holderLease.Reserve(binding, AcceptanceHolderLease.CurrentGeneration());
            ProcessStartInfo start;
            var previousRoot = Environment.GetEnvironmentVariable(OrchestratorDataRoot.EnvironmentVariable);
            try
            {
                Environment.SetEnvironmentVariable(OrchestratorDataRoot.EnvironmentVariable, Data);
                start = ConductorParallelAcceptanceAttemptCoordinator.BuildOwnedProcessStartInfo(attempt, storage,
                    InfrastructureTestSupport.ResolveDotnetHostPath(), [typeof(OrchestratorWorkspace).Assembly.Location]);
            }
            finally { Environment.SetEnvironmentVariable(OrchestratorDataRoot.EnvironmentVariable, previousRoot); }
            // This is the pre-fix failure boundary: the hermetic scrub lost the installation root.
            Assert.Equal(Data, start.Environment[OrchestratorDataRoot.EnvironmentVariable]);
            Assert.Contains(ConductorParallelAcceptanceAttemptCoordinator.OwnedProcessSubcommandName, start.ArgumentList);
            using var child = ProcessTreeGuiSuppression.Start(start)!;
            var identity = TestOwnedProcessStop.Identify(child);
            var stdout = child.StandardOutput.ReadToEndAsync();
            var stderr = child.StandardError.ReadToEndAsync();
            child.StandardInput.Close();
            try
            {
                await child.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(3));
                var output = await stdout;
                var error = await stderr;
                Assert.True(child.ExitCode == 0, $"Owned child exit={child.ExitCode}: {error}\n{output}");
                Assert.Equal("run-attempt-returned", AcceptanceAttemptOwnedPhaseTrail.ReadLastPhase(attempt.MetadataPath));
                Assert.Contains(holderLease.Read(binding).Receipts, receipt => receipt.Reason == "job-custody-ready");
                Assert.Equal(child.Id, holderLease.Read(binding).Holder!.ProcessId);
                var result = JsonSerializer.Deserialize<ConductorParallelAcceptanceRunArtifact>(
                    File.ReadAllText(attempt.ResultPath), JsonOptions)!;
                Assert.Null(result.FaultKind);
                Assert.NotNull(result.Acceptance);
                return Assert.Single(result.Acceptance.UnmetCriteria, check => check.Name == "entry-path crosswalk");
            }
            finally
            {
                if (!child.HasExited)
                {
                    Assert.True(TestOwnedProcessStop.StopTreeIfSame(identity), "Could not stop owned acceptance child.");
                    await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
                }
                await Task.WhenAll(stdout, stderr);
                if (holderLease.Read(binding).Holder is { } holder)
                    await WaitForSettlementAsync(binding, holder);
            }
        }

        private static async Task WaitForSettlementAsync(AcceptanceHolderBinding binding, SpawnProcessIdentity identity)
        {
            // Observe the custodian's atomic publication, including the race before watcher startup.
            var published = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var watcher = new FileSystemWatcher(Path.GetDirectoryName(binding.SettlementPath)!,
                Path.GetFileName(binding.SettlementPath));
            watcher.Created += (_, _) => published.TrySetResult();
            watcher.Renamed += (_, _) => published.TrySetResult();
            watcher.EnableRaisingEvents = true;
            if (!File.Exists(binding.SettlementPath))
                await published.Task.WaitAsync(TimeSpan.FromMinutes(1));
            var settlement = JsonSerializer.Deserialize<AcceptanceJobSettlement>(File.ReadAllText(binding.SettlementPath));
            Assert.Equal(new AcceptanceJobSettlement(binding.JobName, identity, true), settlement);
        }

        private static void Git(string directory, params string[] arguments)
        {
            var result = InfrastructureTestSupport.RunGitProbe(directory, arguments);
            Assert.True(result.Succeeded, $"git {string.Join(' ', arguments)}: {result}");
        }

        public void Dispose() => SharedTestSupport.RemoveTempDirectory(Root);
    }
}
