using System.Diagnostics;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection(TestCollections.ProcessSpawning)]
public sealed class ConductorSelfRelaunchTests
{
    [Xunit.Fact(DisplayName = "ConductorSelfRelaunch_real_binary_build_self_check_and_handoff")]
    public void RealBinaryBuildSelfCheckAndHandoff()
    {
        using var fixture = RealRelaunchFixture.Create();

        var result = ConductorSelfRelaunch.Create(fixture.Options)(
            new ConductorSelfRelaunchRequest("goal-real-success", 7));
        fixture.SuccessorProcessId = result.Handoff?.ProcessId;

        Assert.True(result.HandedOff, result.Reason);
        Assert.NotNull(result.Successor);
        Assert.Contains("schemaVersion=", result.Successor!.SelfCheckDetail, StringComparison.Ordinal);
        Assert.Contains("manifestChecks=", result.Successor.SelfCheckDetail, StringComparison.Ordinal);
        Assert.Contains("lanes=", result.Successor.SelfCheckDetail, StringComparison.Ordinal);
        Assert.Contains("modelFunctions=", result.Successor.SelfCheckDetail, StringComparison.Ordinal);
        Assert.False(fixture.Lease.IsHeld);
        Assert.True(
            WaitUntil(
                () => File.Exists(result.Handoff!.StdoutPath) &&
                    ReadAllTextShared(result.Handoff.StdoutPath!).Contains(
                        "LOOP_STOP tick=",
                        StringComparison.Ordinal),
                TimeSpan.FromSeconds(15)),
            $"The freshly-built successor did not complete its first conductor decision. " +
            $"stdout={ReadAllTextShared(result.Handoff!.StdoutPath!)} " +
            $"stderr={ReadAllTextShared(result.Handoff.StderrPath!)}");
        var successorOutput = ReadAllTextShared(result.Handoff.StdoutPath!);
        Assert.True(
            successorOutput.IndexOf("LOOP_START ", StringComparison.Ordinal) <
            successorOutput.IndexOf("LOOP_STOP tick=", StringComparison.Ordinal),
            $"Successor output did not preserve start-before-first-decision ordering: {successorOutput}");
    }

    [Xunit.Fact(DisplayName = "ConductorSelfRelaunch_real_handoff_failure_stops_successor_and_reacquires_incumbent_lease")]
    public void RealHandoffFailureStopsSuccessorAndReacquiresIncumbentLease()
    {
        using var fixture = RealRelaunchFixture.Create(
            loopStartProbe: (_, _) => false,
            verificationTimeout: TimeSpan.FromMilliseconds(100),
            verificationHardTimeout: TimeSpan.FromSeconds(1),
            loopArgs: ["conduct", "--loop", "--daemon", "--watch", "1", "--max-duration", "30"]);

        var result = ConductorSelfRelaunch.Create(fixture.Options)(
            new ConductorSelfRelaunchRequest("goal-real-handoff-failure", 8));
        fixture.SuccessorProcessId = result.Handoff?.ProcessId;

        Assert.False(result.HandedOff);
        Assert.Equal("handoff", result.FailedPhase);
        Assert.True(fixture.Lease.IsHeld);
        Assert.Contains("rollbackSucceeded=true", result.Handoff!.VerificationOutcome, StringComparison.Ordinal);
        Assert.False(IsProcessAlive(result.Handoff.ProcessId));
    }

    [Xunit.Fact(DisplayName = "ConductorSelfRelaunch_launches_prepared_content_addressed_successor")]
    public void LaunchesPreparedContentAddressedSuccessor()
    {
        var root = CreateTempDirectory();
        try
        {
            ConductLoopHandoffOptions? observedOptions = null;
            var prepared = new ConductorPreparedSuccessor(
                Path.Combine(root, "mcg-run", "abc123"),
                Path.Combine(root, "mcg-run", "abc123", "Mcg.AgentOrchestrator.App.dll"),
                "deadbeef",
                "LOOP_START selfCheck=true");
            var result = ConductorSelfRelaunch.TryRelaunch(
                Options(root),
                new ConductorSelfRelaunchRequest("goal1234", 7),
                _ => prepared,
                (options, _) =>
                {
                    observedOptions = options;
                    return ConductorLoopHandoffResult.StartedProcess(
                        42,
                        "out.log",
                        "err.log",
                        "LOOP_START");
                });

            Assert.True(result.HandedOff);
            Assert.Equal(prepared, result.Successor);
            Assert.Equal(
                ["dotnet", prepared.AppDllPath],
                observedOptions!.SuccessorCommandPrefix);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Xunit.Fact(DisplayName = "ConductorSelfRelaunch_self_check_preparation_failure_keeps_incumbent_authority")]
    public void SelfCheckPreparationFailureKeepsIncumbentAuthority()
    {
        var root = CreateTempDirectory();
        try
        {
            var orchestratorDirectory = Path.Combine(root, ".orchestrator");
            Directory.CreateDirectory(orchestratorDirectory);
            using var lease = ConductorLoopLeaseController.Acquire(orchestratorDirectory);
            var baseOptions = Options(root);
            var options = baseOptions with
            {
                HandoffOptions = baseOptions.HandoffOptions with
                {
                    ReleaseCurrentLease = lease.Release,
                    ReacquireCurrentLease = lease.Reacquire
                }
            };
            var handoffCalled = false;
            var result = ConductorSelfRelaunch.TryRelaunch(
                options,
                new ConductorSelfRelaunchRequest("goal1234", 7),
                _ => throw new ConductorSelfRelaunchPreparationException("self-check", "bad protocol"),
                (_, _) =>
                {
                    handoffCalled = true;
                    return ConductorLoopHandoffResult.Skipped("unexpected");
                });

            Assert.False(result.HandedOff);
            Assert.Equal("self-check", result.FailedPhase);
            Assert.False(handoffCalled);
            Assert.True(lease.IsHeld);
            Assert.Null(result.Handoff);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    private static ConductorSelfRelaunchOptions Options(string root) =>
        new(
            root,
            Path.Combine(root, "App.csproj"),
            Path.Combine(root, "App.dll"),
            Path.Combine(root, "update.ps1"),
            Path.Combine(root, "resolve.ps1"),
            Path.Combine(root, "state.db"),
            Path.Combine(root, "agents.json"),
            Path.Combine(root, "profiles.json"),
            Path.Combine(root, "model-functions.json"),
            "dotnet",
            "powershell",
            new ConductLoopHandoffOptions(
                ["conduct", "--loop"],
                root,
                Path.Combine(root, ".orchestrator"),
                Path.Combine(root, ".orchestrator", "logs"),
                Path.Combine(root, ".orchestrator", "run-events.db"),
                Path.Combine(root, ".conduct-stop"),
                0,
                6,
                () => { }));

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mcg-self-relaunch-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch
        {
        }
    }

    private static bool IsProcessAlive(int? processId)
    {
        if (processId is null)
            return false;

        try
        {
            using var process = Process.GetProcessById(processId.Value);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static bool WaitUntil(Func<bool> predicate, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow.Add(timeout);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (predicate())
                return true;

            Thread.Sleep(50);
        }

        return predicate();
    }

    private static string ReadAllTextShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private sealed class RealRelaunchFixture : IDisposable
    {
        private RealRelaunchFixture(
            string root,
            ConductorLoopLeaseController lease,
            ConductorSelfRelaunchOptions options)
        {
            Root = root;
            Lease = lease;
            Options = options;
        }

        public string Root { get; }
        public ConductorLoopLeaseController Lease { get; }
        public ConductorSelfRelaunchOptions Options { get; }
        public int? SuccessorProcessId { get; set; }

        public static RealRelaunchFixture Create(
            Func<ConductLoopHandoffOptions, long, bool>? loopStartProbe = null,
            TimeSpan verificationTimeout = default,
            TimeSpan verificationHardTimeout = default,
            IReadOnlyList<string>? loopArgs = null)
        {
            var root = CreateTempDirectory();
            var repositoryRoot = InfrastructureTestSupport.FindRepositoryRoot();
            var orchestratorDirectory = Path.Combine(root, ".orchestrator");
            Directory.CreateDirectory(orchestratorDirectory);
            var stateStorePath = Path.Combine(orchestratorDirectory, "state.db");
            _ = CreateMigratedStateRepository(stateStorePath)
                .LoadAsync()
                .GetAwaiter()
                .GetResult();
            var lease = ConductorLoopLeaseController.Acquire(orchestratorDirectory);
            var appOutputDirectory = Path.Combine(root, "build-output");
            var handoffOptions = new ConductLoopHandoffOptions(
                Args: loopArgs ?? ["conduct", "--loop", "--max-iterations", "1"],
                ExecutionDirectory: root,
                OrchestratorDirectory: orchestratorDirectory,
                LogDirectory: Path.Combine(orchestratorDirectory, "logs"),
                RunEventStorePath: Path.Combine(orchestratorDirectory, "run-events.db"),
                StopFilePath: Path.Combine(root, ConductorBatchLoop.StopFileName),
                RenewalCount: 0,
                MaxRenewals: ConductorLoopHandoff.DefaultMaxRenewalsWithoutLanding,
                ReleaseCurrentLease: lease.Release,
                VerificationTimeout: verificationTimeout,
                VerificationHardTimeout: verificationHardTimeout,
                LoopStartProbe: loopStartProbe,
                ReacquireCurrentLease: lease.Reacquire,
                StopFailedSuccessor: ConductorLoopHandoff.StopFailedSuccessor);
            var options = new ConductorSelfRelaunchOptions(
                RepositoryRoot: repositoryRoot,
                AppProjectPath: Path.Combine(repositoryRoot, "src", "Mcg.AgentOrchestrator.App", "Mcg.AgentOrchestrator.App.csproj"),
                AppDllPath: Path.Combine(appOutputDirectory, "Mcg.AgentOrchestrator.App.dll"),
                UpdateHeadMarkerScriptPath: Path.Combine(repositoryRoot, "scripts", "Update-AppDllGitHeadMarker.ps1"),
                ResolveRunDirectoryScriptPath: Path.Combine(repositoryRoot, "scripts", "resolve-run-dir.ps1"),
                StateStorePath: stateStorePath,
                AgentCatalogPath: Path.Combine(orchestratorDirectory, "agents.json"),
                WorkerProfilePath: Path.Combine(orchestratorDirectory, "worker-profiles.json"),
                ModelFunctionCatalogPath: Path.Combine(orchestratorDirectory, "model-functions.json"),
                DotnetPath: Environment.GetEnvironmentVariable("MCG_ORCHESTRATOR_DOTNET_PATH") ?? "dotnet",
                PowerShellPath: "powershell",
                HandoffOptions: handoffOptions,
                BuildTimeout: TimeSpan.FromMinutes(3),
                SelfCheckTimeout: TimeSpan.FromSeconds(30));
            return new RealRelaunchFixture(root, lease, options);
        }

        public void Dispose()
        {
            if (SuccessorProcessId is { } processId)
            {
                try
                {
                    ConductorLoopHandoff.StopFailedSuccessor(processId);
                }
                catch
                {
                }
            }

            Lease.Dispose();
            TryDeleteDirectory(Root);
        }
    }
}
