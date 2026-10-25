using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;
using Xunit;

// Parallel-safe: actual managed children with allow-listed environments and unique stores/roots.
public sealed class WorkspaceInvocationStartupTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DetachedLauncherRefusesConflictingBindingBeforeCreatingLogs(bool rawFlags)
    {
        using var fixture = new Fixture();
        var host = Environment.ProcessPath;
        Assert.NotNull(host);
        Assert.Equal("dotnet", Path.GetFileNameWithoutExtension(host), ignoreCase: true);
        const string probeName = "Mcg.AgentOrchestrator.IsolatedDotnetProbe";
        var probeDirectory = FixtureProbeLocator.FindDirectory(typeof(WorkspaceInvocationStartupTests), probeName, probeName + ".dll");
        Assert.True(probeDirectory is not null, "Managed host probe missing. Tried: " + string.Join(", ",
            FixtureProbeLocator.TriedPaths(typeof(WorkspaceInvocationStartupTests), probeName, probeName + ".dll")));
        var hostReceipt = Path.Combine(fixture.Root, "preflight-host.txt");
        var startupHook = Path.Combine(probeDirectory!, probeName + ".dll");
        var sourceRoot = VerifiedRepositoryRoot.Find();
        var before = fixture.Snapshot();
        var missingHost = fixture.RunLauncher(sourceRoot, rawFlags, "dotnet", startupHook, hostReceipt);
        Assert.NotEqual(0, missingHost.ExitCode);
        Assert.False(File.Exists(hostReceipt), "A bare host must be unavailable in the controlled launcher environment.");
        Assert.Equal(before, fixture.Snapshot());

        var result = fixture.RunLauncher(sourceRoot, rawFlags, host, startupHook, hostReceipt);
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("binding", result.StandardError, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(hostReceipt), "Detached preflight must use the configured managed host.");
        using var receipt = JsonDocument.Parse(File.ReadAllText(hostReceipt));
        Assert.Equal(host, receipt.RootElement.GetProperty("host").GetString(), ignoreCase: true);
        var arguments = receipt.RootElement.GetProperty("arguments").EnumerateArray().Select(value => value.GetString()).ToArray();
        Assert.Equal(new[] { typeof(OrchestratorWorkspace).Assembly.Location, "__workspace-binding-check", "--json",
            "--repository-root", fixture.Intended, "--expected-state-directory", fixture.State, "--disposable-fixture" },
            arguments.SelectMany(value => value!.StartsWith("--", StringComparison.Ordinal) && value.Contains('=')
                ? value.Split('=', 2) : new[] { value }).ToArray());
        File.Delete(hostReceipt); // Only the host receipt may change; all workspace bytes must remain identical.
        Assert.Equal(before, fixture.Snapshot());
        Assert.False(Directory.Exists(Path.Combine(fixture.State, "logs")));
    }

    [Theory]
    [InlineData("unbound")]
    [InlineData("root")]
    [InlineData("project")]
    [InlineData("tenant")]
    [InlineData("state")]
    public void FixtureRefusalFromWrongCwdLeavesBothTreesUnchanged(string conflict)
    {
        using var fixture = new Fixture();
        string[] args = conflict == "unbound" ? ["conductor", "setup", "--disposable-fixture"] : fixture.Bound("conductor", "setup");
        var environment = new Dictionary<string, string>();
        if (conflict == "root") environment[OrchestratorWorkspace.RepoRootEnvironmentVariable] = fixture.Decoy;
        if (conflict == "project")
        {
            environment[OrchestratorProjectSelection.ProjectEnvironmentVariable] = "decoy";
            args = args.Concat(["--project=default"]).ToArray();
        }
        if (conflict == "tenant")
        {
            environment[OrchestratorTenantSelection.TenantEnvironmentVariable] = "decoy";
            args = args.Concat(["--tenant=default"]).ToArray();
        }
        if (conflict == "state") args = args.Select(value => value == fixture.State ? Path.Combine(fixture.Decoy, ".orchestrator") : value).ToArray();
        var before = fixture.Snapshot();
        var result = fixture.Run(args, environment);
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(conflict == "unbound" ? "requires" : conflict == "state" ? "differs" : "conflicts", result.StandardError);
        Assert.Equal(before, fixture.Snapshot());
    }

    [Fact]
    public void PlanIsNoncreatingAndCorrectBindingFromDecoyCwdTouchesOnlyIntendedStores()
    {
        using var fixture = new Fixture();
        var before = fixture.Snapshot();
        var plan = fixture.Run(fixture.Bound("conductor", "setup", "--plan"));
        Assert.Equal(0, plan.ExitCode);
        Assert.Contains(fixture.State, plan.StandardOutput);
        Assert.Contains("state-migrations", plan.StandardOutput);
        Assert.Contains("action migrate", plan.StandardOutput);
        Assert.Equal(before, fixture.Snapshot());

        var decoy = fixture.Snapshot(fixture.Decoy);
        var setup = fixture.Run(fixture.Bound("conductor", "setup"));
        Assert.Equal(0, setup.ExitCode);
        Assert.NotEqual(before, fixture.Snapshot());
        Assert.Equal(decoy, fixture.Snapshot(fixture.Decoy));
        Assert.True(File.Exists(Path.Combine(fixture.State, "portfolio.db")));
        Assert.True(StateDbMigrations.IsUpToDate(Path.Combine(fixture.State, "state.db")));
        var current = fixture.Snapshot();
        var repeat = fixture.Run(fixture.Bound("conductor", "setup"));
        Assert.Equal(0, repeat.ExitCode);
        Assert.Equal(current, fixture.Snapshot());
    }

    [Fact]
    public void NegativeControlWithoutFixtureBindingPreservesAuthorizedInheritedProductionSelector()
    {
        using var fixture = new Fixture();
        var intended = fixture.Snapshot(fixture.Intended);
        var result = fixture.Run(["conductor", "setup"], new()
        {
            [OrchestratorWorkspace.RepoRootEnvironmentVariable] = fixture.Decoy
        });
        Assert.Equal(0, result.ExitCode);
        Assert.True(File.Exists(Path.Combine(fixture.Decoy, ".orchestrator", "portfolio.db")));
        Assert.Equal(intended, fixture.Snapshot(fixture.Intended));
    }

    [Fact]
    public void PersistedProjectRedirectionRefusesBeforeAnyStoreOrLauncherWrite()
    {
        using var fixture = new Fixture();
        var registry = new OrchestratorProjectRegistry(fixture.Registry, fixture.Data);
        registry.CreateProject("decoy", fixture.Decoy);
        File.WriteAllText(registry.CurrentProjectPath, "decoy");
        var before = fixture.Snapshot();
        var result = fixture.Run(fixture.Bound("conductor", "setup"));
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("differs from explicit root", result.StandardError);
        Assert.Equal(before, fixture.Snapshot());
    }

    [Theory]
    [InlineData("data")]
    [InlineData("registry")]
    [InlineData("relocation")]
    public void InheritedDataRegistryAndRelocationCannotRedirectBoundTargets(string selector)
    {
        using var fixture = new Fixture();
        var registry = new OrchestratorProjectRegistry(fixture.Registry, fixture.Data);
        var environment = new Dictionary<string, string>();
        var args = fixture.Bound("conductor", "setup");
        if (selector == "relocation")
            registry.RecordDefaultStateLocation(new(fixture.Intended, "relocated", ".orchestrator.backup-fixture", DateTimeOffset.UnixEpoch));
        else
        {
            registry.CreateProject("alpha", fixture.Intended);
            args = args.Select(value => value == fixture.State ? Path.Combine(fixture.Data, "projects", "alpha") : value)
                .Concat(["--project=alpha"]).ToArray();
            if (selector == "data") environment["MCG_ORCHESTRATOR_DATA"] = Path.Combine(fixture.Root, "other-data");
            else
            {
                var other = new OrchestratorProjectRegistry(Path.Combine(fixture.Root, "other-registry"), fixture.Data);
                other.CreateProject("alpha", fixture.Decoy);
                environment[OrchestratorProjectRegistry.RegistryHomeEnvironmentVariable] = other.RegistryDirectory;
            }
        }
        var before = fixture.Snapshot();
        var result = fixture.Run(args, environment);
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("differs", result.StandardError);
        Assert.Equal(before, fixture.Snapshot());
    }

    [Fact]
    public void CorrectProjectTenantRelocationBindingPreservesAllBoundaries()
    {
        using var fixture = new Fixture();
        var registry = new OrchestratorProjectRegistry(fixture.Registry, fixture.Data);
        registry.RecordDefaultStateLocation(new(fixture.Intended, "relocated", ".orchestrator.backup-fixture", DateTimeOffset.UnixEpoch));
        var state = Path.Combine(fixture.Data, "relocated", "tenants", "alpha");
        var args = fixture.Bound("conductor", "setup").Select(value => value == fixture.State ? state : value)
            .Concat(["--tenant=alpha"]).ToArray();
        var intended = fixture.Snapshot(fixture.Intended);
        var decoy = fixture.Snapshot(fixture.Decoy);
        var result = fixture.Run(args);
        Assert.Equal(0, result.ExitCode);
        Assert.True(File.Exists(Path.Combine(state, "portfolio.db")));
        Assert.False(File.Exists(Path.Combine(fixture.Data, "relocated", "portfolio.db")));
        Assert.Equal(intended, fixture.Snapshot(fixture.Intended));
        Assert.Equal(decoy, fixture.Snapshot(fixture.Decoy));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OlderRuntimeRefusesActualNewerStoreBeforeCreatingStateOrEarlierStores(bool loop)
    {
        using var fixture = new Fixture();
        Directory.CreateDirectory(fixture.State);
        var path = Path.Combine(fixture.State, "backlog.db");
        BacklogStore.Setup(path);
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE store_schema_versions SET version = 99";
            Assert.Equal(1, command.ExecuteNonQuery());
        }
        var before = fixture.Snapshot();
        var result = fixture.Run(loop ? fixture.Bound("conduct", "--loop", "--max-iterations", "1") : fixture.Bound("conductor", "setup"));
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("99", result.StandardError);
        Assert.Contains("compatible runtime", result.StandardError);
        Assert.DoesNotContain("run setup", result.StandardError);
        Assert.Equal(before, fixture.Snapshot());
        Assert.False(File.Exists(Path.Combine(fixture.State, "portfolio.db")));
        Assert.False(File.Exists(Path.Combine(fixture.State, "state.db")));
    }

    [Fact]
    public void DogfoodLogListReportsCompatibleRuntimeForNewerStoreWithoutWrites()
    {
        using var fixture = new Fixture();
        Directory.CreateDirectory(fixture.State);
        StateDbMigrations.EnsureUpToDate(Path.Combine(fixture.State, "state.db"));
        var path = Path.Combine(fixture.State, "dogfood-log.db");
        DogfoodLogStore.Setup(path);
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE store_schema_versions SET version = 99";
            Assert.Equal(1, command.ExecuteNonQuery());
        }
        var before = fixture.Snapshot();
        var result = fixture.Run(fixture.Bound("dogfood-log", "list", "--limit", "1"));
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains($"Store 'dogfood-log' at '{path}' records schema version 99", result.StandardError);
        Assert.Contains($"newer than runtime-supported version {StoreSchemaRegistry.DogfoodLog.CurrentVersion}", result.StandardError);
        Assert.Contains("compatible runtime or independently verified recovery", result.StandardError);
        Assert.DoesNotContain("run setup", result.StandardError);
        Assert.Equal(before, fixture.Snapshot());
    }

    [Fact]
    public void DogfoodLogListWithZeroStateWriteThresholdLeavesWorkspaceUnchanged()
    {
        using var fixture = new Fixture();
        Directory.CreateDirectory(fixture.State);
        StateDbMigrations.EnsureUpToDate(Path.Combine(fixture.State, "state.db"));
        var path = Path.Combine(fixture.State, "dogfood-log.db");
        DogfoodLogStore.Setup(path);
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE store_schema_versions SET version = 99";
            Assert.Equal(1, command.ExecuteNonQuery());
        }
        var before = fixture.Snapshot();
        var result = fixture.Run(fixture.Bound("dogfood-log", "list", "--limit", "1"),
            new() { ["MCG_STATE_WRITE_WARNING_MS"] = "0" });
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("compatible runtime or independently verified recovery", result.StandardError);
        Assert.DoesNotContain("run setup", result.StandardError);
        Assert.Equal(before, fixture.Snapshot());
    }

    [Fact]
    public void StateWriteTransactionVerbWithZeroThresholdStillWritesDiagnostics()
    {
        using var fixture = new Fixture();
        Directory.CreateDirectory(fixture.State);
        StateDbMigrations.EnsureUpToDate(Path.Combine(fixture.State, "state.db"));

        // epic-suggest needs the kernel, is absent from SkipsKernelState and reaches TransactWithOutboxAsync.
        var result = fixture.Run(fixture.Bound("epic-suggest"),
            new() { ["MCG_STATE_WRITE_WARNING_MS"] = "0" });

        Assert.Equal(0, result.ExitCode);
        var receipt = Path.Combine(fixture.State, SqliteWriteTelemetry.DiagnosticsDirectoryName, SqliteWriteTelemetry.DiagnosticsFileName);
        Assert.True(File.Exists(receipt));
        Assert.True(new FileInfo(receipt).Length > 0);
    }

    [Fact]
    public void BareAgentsWithZeroThresholdServesReadRouteWithoutDiagnostics()
    {
        using var fixture = new Fixture();
        Directory.CreateDirectory(fixture.State);
        StateDbMigrations.EnsureUpToDate(Path.Combine(fixture.State, "state.db"));

        var result = fixture.Run(fixture.Bound("agents"),
            new() { ["MCG_STATE_WRITE_WARNING_MS"] = "0" });

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Agents:", result.StandardOutput);
        var receipt = Path.Combine(fixture.State, SqliteWriteTelemetry.DiagnosticsDirectoryName, SqliteWriteTelemetry.DiagnosticsFileName);
        Assert.False(File.Exists(receipt));
    }

    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = Directory.CreateTempSubdirectory("workspace-startup-").FullName;
        internal string Intended => Path.Combine(Root, "intended");
        internal string Decoy => Path.Combine(Root, "decoy");
        internal string State => Path.Combine(Intended, ".orchestrator");
        internal string Registry => Path.Combine(Root, "registry");
        internal string Data => Path.Combine(Root, "data");
        internal Fixture()
        {
            Directory.CreateDirectory(Path.Combine(Intended, ".git"));
            Directory.CreateDirectory(Path.Combine(Decoy, ".git"));
        }
        internal string[] Bound(params string[] command) => command.Concat([
            "--repository-root", Intended, "--expected-state-directory", State, "--disposable-fixture"]).ToArray();
        internal CliChildProcessResult Run(string[] args, Dictionary<string, string>? selectors = null)
        {
            var start = new ProcessStartInfo("dotnet")
            {
                WorkingDirectory = Decoy, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
            };
            ConfigureEnvironment(start, selectors);
            start.ArgumentList.Add(typeof(OrchestratorWorkspace).Assembly.Location);
            foreach (var arg in args) start.ArgumentList.Add(arg);
            return CliChildProcessRunner.Run(start);
        }
        internal CliChildProcessResult RunLauncher(string sourceRoot, bool rawFlags, string host, string startupHook, string hostReceipt)
        {
            var start = new ProcessStartInfo(WorkerShell.Executable)
            {
                WorkingDirectory = Decoy, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
            };
            ConfigureEnvironment(start, new() { [OrchestratorWorkspace.RepoRootEnvironmentVariable] = Decoy });
            // Require the explicit configured host; falling back to bare dotnet cannot satisfy this probe.
            start.Environment["PATH"] = string.Empty;
            start.Environment["MCG_ORCHESTRATOR_DOTNET_PATH"] = host;
            start.Environment["DOTNET_STARTUP_HOOKS"] = startupHook;
            start.Environment["MCG_WORKSPACE_BINDING_HOST_RECEIPT"] = hostReceipt;
            foreach (var arg in WorkerShell.BaseArguments())
                start.ArgumentList.Add(arg);
            // -File reparses an unquoted --selector=C:\path as a parameter/value at the drive colon.
            // Literal command arguments preserve the raw CLI selectors passed to the actual launcher.
            static string Literal(string value) => "'" + value.Replace("'", "''") + "'";
            var invocation = "& " + Literal(Path.Combine(sourceRoot, "scripts", "Start-OrchestratorCommand.ps1")) +
                " -AppDll " + Literal(typeof(OrchestratorWorkspace).Assembly.Location);
            if (rawFlags)
            {
                invocation += " " + string.Join(" ", new[] { "conductor", "setup", $"--repository-root={Intended}",
                    $"--expected-state-directory={State}", "--disposable-fixture" }.Select(Literal));
            }
            else
            {
                invocation += " -RepositoryRoot " + Literal(Intended) + " -ExpectedStateDirectory " + Literal(State) +
                    " -DisposableFixture 'conductor' 'setup'";
            }
            start.ArgumentList.Add(invocation);
            return CliChildProcessRunner.Run(start);
        }
        private void ConfigureEnvironment(ProcessStartInfo start, Dictionary<string, string>? selectors)
        {
            start.Environment.Clear();
            foreach (var key in new[] { "PATH", "SystemRoot", "TEMP", "TMP", "DOTNET_ROOT", "ProgramFiles", "USERPROFILE", "LOCALAPPDATA" })
                if (Environment.GetEnvironmentVariable(key) is { } value) start.Environment[key] = value;
            // PowerShell needs .EXE in PATHEXT to invoke and wait for the configured native host.
            // Without it the host can outlive the shell and retain the fixture's current directory.
            start.Environment["PATHEXT"] = ".COM;.EXE;.BAT;.CMD";
            start.Environment[OrchestratorProjectRegistry.RegistryHomeEnvironmentVariable] = Registry;
            start.Environment["MCG_ORCHESTRATOR_DATA"] = Data;
            start.Environment["MCG_ORCHESTRATOR_HOME"] = Path.Combine(Root, "home");
            start.Environment["MCG_DOTNET_ISOLATED_ROOT"] = Path.Combine(Root, "dotnet");
            start.Environment["OLLAMA_BASE_URL"] = "http://127.0.0.1:1";
            if (selectors is not null) foreach (var (key, value) in selectors) start.Environment[key] = value;
        }
        internal string[] Snapshot(string? root = null) => Directory.EnumerateFileSystemEntries(root ?? Root, "*", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal).Select(path => Path.GetRelativePath(root ?? Root, path) +
                (File.Exists(path) ? ":" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) : ":directory")).ToArray();
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
