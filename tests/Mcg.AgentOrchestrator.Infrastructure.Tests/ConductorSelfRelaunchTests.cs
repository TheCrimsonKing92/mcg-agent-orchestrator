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

        var result = ConductorSelfRelaunch.Create(fixture.OptionsWithCapturedSuccessorIdentity)(
            new ConductorSelfRelaunchRequest("goal-real-success", 7));

        Assert.True(result.HandedOff, result.Reason);
        Assert.NotNull(result.Successor);
        Assert.False(Directory.Exists(Path.GetDirectoryName(fixture.Options.AppDllPath)));
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
            loopArgs: ["conduct", "--loop", "--daemon", "--watch", "1", "--max-duration", "30"],
            usePrebuiltPayload: true);

        var result = ConductorSelfRelaunch.Create(fixture.OptionsWithCapturedSuccessorIdentity)(
            new ConductorSelfRelaunchRequest("goal-real-handoff-failure", 8));

        Assert.False(result.HandedOff);
        Assert.Equal("handoff", result.FailedPhase);
        Assert.True(fixture.Lease.IsHeld);
        Assert.Contains("rollbackSucceeded=true", result.Handoff!.VerificationOutcome, StringComparison.Ordinal);
        Assert.False(IsProcessAlive(result.Handoff.ProcessId));
    }

    [Xunit.Fact(DisplayName = "ConductorSelfRelaunch_real_successor_process_failure_is_classified_as_self_check")]
    public void RealSuccessorProcessFailureIsClassifiedAsSelfCheck()
    {
        using var fixture = RealRelaunchFixture.Create(usePrebuiltPayload: true);
        File.WriteAllText(fixture.Options.StateStorePath, "not-a-sqlite-database");

        var result = ConductorSelfRelaunch.Create(fixture.Options)(
            new ConductorSelfRelaunchRequest("goal-real-self-check-failure", 9));

        Assert.False(result.HandedOff);
        Assert.Equal("self-check", result.FailedPhase);
        Assert.False(Directory.Exists(Path.GetDirectoryName(fixture.Options.AppDllPath)));
    }

    [Xunit.Fact]
    public void Build_failure_removes_partial_successor_output()
    {
        using var fixture = RealRelaunchFixture.Create();
        var project = Path.Combine(fixture.Root, "partial-build.csproj");
        var buildProof = Path.Combine(fixture.Root, "partial-build-executed.txt");
        File.WriteAllText(project, $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
              <Target Name="FailAfterPartialOutput" BeforeTargets="Build">
                <MakeDir Directories="$(OutputPath)" />
                <WriteLinesToFile File="$(OutputPath)/partial.txt" Lines="partial" Overwrite="true" />
                <Error Condition="!Exists('$(OutputPath)/partial.txt')" Text="partial output was not written" />
                <WriteLinesToFile File="{buildProof}" Lines="executed" Overwrite="true" />
                <Error Text="intentional partial build failure" />
              </Target>
            </Project>
            """);

        var result = ConductorSelfRelaunch.Create(fixture.Options with { AppProjectPath = project })(
            new ConductorSelfRelaunchRequest("goal-partial-build", 10));

        Assert.False(result.HandedOff);
        Assert.Equal("build", result.FailedPhase);
        Assert.True(File.Exists(buildProof), result.Reason);
        Assert.False(Directory.Exists(Path.GetDirectoryName(fixture.Options.AppDllPath)));
    }

    [Xunit.Fact]
    public void Resolver_failure_removes_built_successor_output()
    {
        using var fixture = RealRelaunchFixture.Create(usePrebuiltPayload: true);
        File.WriteAllText(fixture.Options.ResolveRunDirectoryScriptPath,
            "param([string]$Dll)\r\nWrite-Error 'intentional resolver failure'\r\nexit 13\r\n");

        var result = ConductorSelfRelaunch.Create(fixture.Options)(
            new ConductorSelfRelaunchRequest("goal-resolver-failure", 11));

        Assert.False(result.HandedOff);
        Assert.Equal("stage", result.FailedPhase);
        Assert.Contains("publish content-addressed run directory", result.Reason);
        Assert.False(Directory.Exists(Path.GetDirectoryName(fixture.Options.AppDllPath)));
    }

    [Xunit.Fact]
    public void Missing_prebuilt_output_fails_build_without_fallback()
    {
        using var fixture = RealRelaunchFixture.Create(usePrebuiltPayload: true);
        var missingOutput = Path.Combine(fixture.Root, "missing-prebuilt-app");

        var result = ConductorSelfRelaunch.Create(
            fixture.Options with { PrebuiltAppOutputDirectory = missingOutput })(
            new ConductorSelfRelaunchRequest("goal-missing-prebuilt-output", 12));

        Assert.False(result.HandedOff);
        Assert.Equal("build", result.FailedPhase);
        Assert.Contains(missingOutput, result.Reason, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.GetDirectoryName(fixture.Options.AppDllPath)));
    }

    [Xunit.Fact(DisplayName = "ConductorSelfRelaunch_launches_prepared_content_addressed_successor")]
    public void LaunchesPreparedContentAddressedSuccessor()
    {
        var root = CreateTempDirectory();
        try
        {
            ConductLoopHandoffOptions? observedOptions = null;
            using var runDirectoryLease = new MemoryStream();
            var prepared = new ConductorPreparedSuccessor(
                Path.Combine(root, "mcg-run", "abc123"),
                Path.Combine(root, "mcg-run", "abc123", "Mcg.AgentOrchestrator.App.dll"),
                "deadbeef",
                "deadbeef",
                "LOOP_START selfCheck=true",
                runDirectoryLease);
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
            Assert.Throws<ObjectDisposedException>(() => runDirectoryLease.ReadByte());
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Xunit.Fact]
    public void StagedSourceCommit_MatchingMarker_IsReturned()
    {
        var root = CreateTempDirectory();
        try
        {
            File.WriteAllText(Path.Combine(root, "App.dll.git-head"), "deadbeef\r\n");

            var actual = ConductorSelfRelaunch.ReadStagedSourceCommit(
                root,
                "App.dll",
                "deadbeef");

            Assert.Equal("deadbeef", actual);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Xunit.Fact]
    public void StagedSourceCommit_DivergentMarker_FailsStage()
    {
        var root = CreateTempDirectory();
        try
        {
            File.WriteAllText(Path.Combine(root, "App.dll.git-head"), "old-head");

            var error = Assert.Throws<ConductorSelfRelaunchPreparationException>(() =>
                ConductorSelfRelaunch.ReadStagedSourceCommit(root, "App.dll", "new-head"));

            Assert.Equal("stage", error.Phase);
            Assert.Contains("did not match repository HEAD", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Xunit.Fact]
    public void StagedSourceCommit_MissingMarker_FailsStage()
    {
        var root = CreateTempDirectory();
        try
        {
            var error = Assert.Throws<ConductorSelfRelaunchPreparationException>(() =>
                ConductorSelfRelaunch.ReadStagedSourceCommit(root, "App.dll", "new-head"));

            Assert.Equal("stage", error.Phase);
            Assert.Contains("could not be read", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Xunit.Fact]
    public void StagedSourceCommit_EmptyMarker_FailsStage()
    {
        var root = CreateTempDirectory();
        try
        {
            File.WriteAllText(Path.Combine(root, "App.dll.git-head"), "  \r\n");

            var error = Assert.Throws<ConductorSelfRelaunchPreparationException>(() =>
                ConductorSelfRelaunch.ReadStagedSourceCommit(root, "App.dll", "new-head"));

            Assert.Equal("stage", error.Phase);
            Assert.Contains("was empty", error.Message, StringComparison.Ordinal);
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
        private readonly int _appBuildInvocationCountAtStart;
        private readonly IReadOnlyDictionary<string, string>? _sharedPayloadHashesAtStart;
        private readonly bool _usePrebuiltPayload;

        private RealRelaunchFixture(
            string root,
            ConductorLoopLeaseController lease,
            ConductorSelfRelaunchOptions options,
            bool usePrebuiltPayload,
            IReadOnlyDictionary<string, string>? sharedPayloadHashesAtStart)
        {
            Root = root;
            Lease = lease;
            Options = options;
            _usePrebuiltPayload = usePrebuiltPayload;
            _sharedPayloadHashesAtStart = sharedPayloadHashesAtStart;
            _appBuildInvocationCountAtStart = ConductorSelfRelaunch.AppBuildInvocationCount;
        }

        public string Root { get; }
        public ConductorLoopLeaseController Lease { get; }
        public ConductorSelfRelaunchOptions Options { get; }
        public ConductorSupervisorProcessIdentity? SuccessorIdentity { get; set; }
        public ConductorSelfRelaunchOptions OptionsWithCapturedSuccessorIdentity => Options with
        {
            HandoffOptions = Options.HandoffOptions with
            {
                SuccessorReadyProbe = (launched, request) =>
                {
                    SuccessorIdentity = launched.SuccessorIdentity;
                    return ConductorLoopHandoff.HasSuccessorReadySignal(launched, request);
                }
            }
        };

        public static RealRelaunchFixture Create(
            Func<ConductLoopHandoffOptions, long, bool>? loopStartProbe = null,
            TimeSpan verificationTimeout = default,
            TimeSpan verificationHardTimeout = default,
            IReadOnlyList<string>? loopArgs = null,
            bool usePrebuiltPayload = false)
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
            var resolverWrapper = Path.Combine(root, "resolve-test-run-dir.ps1");
            var isolatedTemp = Path.Combine(root, "isolated-temp");
            Directory.CreateDirectory(isolatedTemp);
            static string Quote(string value) => value.Replace("'", "''", StringComparison.Ordinal);
            File.WriteAllText(resolverWrapper, string.Join(Environment.NewLine,
                "param([string]$Dll)",
                "$ErrorActionPreference = 'Stop'",
                $"$env:TEMP = '{Quote(isolatedTemp)}'",
                $"$env:TMP = '{Quote(isolatedTemp)}'",
                $"& '{Quote(Path.Combine(repositoryRoot, "scripts", "resolve-run-dir.ps1"))}' -Dll $Dll"));
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
                ResolveRunDirectoryScriptPath: resolverWrapper,
                StateStorePath: stateStorePath,
                AgentCatalogPath: Path.Combine(orchestratorDirectory, "agents.json"),
                WorkerProfilePath: Path.Combine(orchestratorDirectory, "worker-profiles.json"),
                ModelFunctionCatalogPath: Path.Combine(orchestratorDirectory, "model-functions.json"),
                DotnetPath: Environment.GetEnvironmentVariable("MCG_ORCHESTRATOR_DOTNET_PATH") ?? "dotnet",
                PowerShellPath: "powershell",
                HandoffOptions: handoffOptions,
                BuildTimeout: TimeSpan.FromMinutes(3),
                SelfCheckTimeout: TimeSpan.FromSeconds(30));
            IReadOnlyDictionary<string, string>? sharedPayloadHashes = null;
            if (usePrebuiltPayload)
            {
                sharedPayloadHashes = ConductorSelfRelaunchSharedAppPayload.SnapshotHashes();
                var privateCopy = Path.Combine(root, "prebuilt-app");
                ConductorSelfRelaunchSharedAppPayload.CreatePrivateCopy(privateCopy);
                Assert.NotEqual(ConductorSelfRelaunchSharedAppPayload.DirectoryPath, privateCopy);
                Assert.True(privateCopy.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
                Assert.True(File.Exists(Path.Combine(privateCopy, Path.GetFileName(options.AppDllPath))));
                Assert.All(Directory.EnumerateFiles(privateCopy, "*", SearchOption.AllDirectories),
                    file => Assert.False(File.GetAttributes(file).HasFlag(FileAttributes.ReadOnly)));
                options = options with { PrebuiltAppOutputDirectory = privateCopy };
            }
            else
            {
                Assert.Null(options.PrebuiltAppOutputDirectory);
                Assert.Null(options.Staging.PrebuiltAppOutputDirectory);
            }

            return new RealRelaunchFixture(root, lease, options, usePrebuiltPayload, sharedPayloadHashes);
        }

        public void Dispose()
        {
            TestOwnedProcessStop.StopTreeIfSame(SuccessorIdentity);

            Lease.Dispose();
            TryDeleteDirectory(Root);
            Assert.Equal(
                _usePrebuiltPayload ? 0 : 1,
                ConductorSelfRelaunch.AppBuildInvocationCount - _appBuildInvocationCountAtStart);
            if (_sharedPayloadHashesAtStart is not null)
            {
                ConductorSelfRelaunchSharedAppPayload.AssertHashesEqual(_sharedPayloadHashesAtStart);
            }
        }
    }
}
