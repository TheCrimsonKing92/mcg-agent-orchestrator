using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// These tests mutate the process-global MCG_DOTNET_ISOLATED_ROOT env var (via EnvVarScope). xUnit
// runs distinct test classes in parallel, so without a shared collection they clobber each other's
// root and flake. Pinning every env-var-mutating class to one non-parallel collection serializes them.
[Xunit.CollectionDefinition("IsolatedDotnetRoot", DisableParallelization = true)]
public sealed class IsolatedDotnetRootCollection : Xunit.ICollectionFixture<IsolatedDotnetRootFixture>
{
}

// Pins MCG_DOTNET_ISOLATED_ROOT to an ephemeral per-run temp root for the ENTIRE IsolatedDotnetRoot
// collection, so lease-acquiring tests (GoalAcceptanceVerifier/LocalProcessVerifier, which do NOT set
// their own per-test scope) never touch the shared firewall slots (e.g. slot-0). Without this, when the
// suite itself runs inside a slot-routed harness holding slot-0, those tests deadlock waiting for the
// lease execution lock their own host process already holds. Per-test EnvVarScope overrides nest cleanly
// on top of this baseline (they save/restore whatever value is current).
public sealed class IsolatedDotnetRootFixture : IDisposable
{
    private readonly string? _originalValue;
    private readonly string _root;

    public IsolatedDotnetRootFixture()
    {
        _originalValue = Environment.GetEnvironmentVariable(DotnetBuildEnvironmentManager.IsolatedRootOverrideVariable);
        _root = Path.Combine(Path.GetTempPath(), $"{DotnetBuildEnvironmentManager.RootDirectoryName}-collection-{Guid.NewGuid():N}");
        Environment.SetEnvironmentVariable(DotnetBuildEnvironmentManager.IsolatedRootOverrideVariable, _root);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(DotnetBuildEnvironmentManager.IsolatedRootOverrideVariable, _originalValue);
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // Best effort.
        }
    }
}

[Xunit.Collection("IsolatedDotnetRoot")]
public sealed class DotnetBuildEnvironmentManagerTests
{
    [Xunit.Fact(DisplayName = "DotnetBuildEnvironmentManager_reuses_goal_lease_with_metadata_and_cleanup")]
    public void DotnetBuildEnvironmentManagerReusesGoalLeaseWithMetadataAndCleanup()
    {
        using var _ = EnvVarScope.ForIsolatedDotnetRoot();
        var goalId = new GoalId("feedbeeffeedbeeffeedbeeffeedbeef");
        try
        {
            var first = DotnetBuildEnvironmentManager.CreateAttempt(goalId, "Acceptance");
            var second = DotnetBuildEnvironmentManager.CreateAttempt(goalId, "Acceptance");

            Assert.Equal(DotnetBuildEnvironmentManager.GoalRoot(goalId), first.RootPath);
            Assert.Equal("goal-feedbeef", first.LeaseId);
            Assert.Equal(first.LeaseId, second.LeaseId);
            Assert.Equal(first.RootPath, second.RootPath);
            Assert.Equal(first.ArtifactsPath, second.ArtifactsPath);
            Assert.True(second.ReusedGoalLease);
            Assert.Equal(DotnetBuildEnvironmentManager.GoalArtifactsPath(goalId), first.ArtifactsPath);
            Assert.True(first.ArtifactsPath.Contains(Path.Combine("slots", "slot-"), StringComparison.OrdinalIgnoreCase));
            Assert.True(Directory.Exists(first.ArtifactsPath));
            Assert.True(Directory.Exists(second.ArtifactsPath));
            Assert.False(string.IsNullOrWhiteSpace(second.LeaseMetadataPath));
            Assert.True(File.Exists(second.LeaseMetadataPath));
            Assert.True(first.Arguments.Contains("--artifacts-path"));
            Assert.True(first.Arguments.Contains("--disable-build-servers"));
            Assert.True(first.Arguments.Contains(first.ArtifactsPath));
            var otherGoalId = new GoalId("cafebabecafebabecafebabecafebabe");
            var other = DotnetBuildEnvironmentManager.CreateAttempt(otherGoalId, "Acceptance");
            Assert.True(other.ArtifactsPath.Contains(Path.Combine("slots", "slot-"), StringComparison.OrdinalIgnoreCase));
            Assert.True(DotnetBuildEnvironmentManager.TryDeleteGoalArtifacts(otherGoalId));
            using var metadata = JsonDocument.Parse(File.ReadAllText(second.LeaseMetadataPath!));
            Assert.Equal(goalId.Value, metadata.RootElement.GetProperty("goalId").GetString());
            Assert.Equal(first.LeaseId, metadata.RootElement.GetProperty("leaseId").GetString());
            Assert.Equal(second.ArtifactsPath, metadata.RootElement.GetProperty("artifactsPath").GetString());

            Assert.True(DotnetBuildEnvironmentManager.TryDeleteGoalArtifacts(goalId));
            Assert.False(Directory.Exists(first.RootPath));
        }
        finally
        {
            DotnetBuildEnvironmentManager.TryDeleteGoalArtifacts(goalId);
        }
    }

    [Xunit.Fact(DisplayName = "DotnetBuildEnvironmentManager_reuses_stable_manual_slot")]
    public void DotnetBuildEnvironmentManagerReusesStableManualSlot()
    {
        var first = DotnetBuildEnvironmentManager.CreateAttempt(null, "Acceptance");
        var second = DotnetBuildEnvironmentManager.CreateAttempt(null, "Retry");

        Assert.Equal("run-slot-manual", first.LeaseId);
        Assert.Equal(first.RootPath, second.RootPath);
        Assert.Equal(first.ArtifactsPath, second.ArtifactsPath);
        Assert.True(first.ArtifactsPath.Contains(Path.Combine("slots", "manual", "artifacts"), StringComparison.OrdinalIgnoreCase));
        Assert.Equal(first.ExecutionLockPath, second.ExecutionLockPath);
        Assert.True(first.Arguments.Contains(first.ArtifactsPath));
    }

    [Xunit.Fact(DisplayName = "DotnetBuildEnvironmentManager_detects_stale_locks_and_rotates_goal_lease")]
    public void DotnetBuildEnvironmentManagerDetectsStaleLocksAndRotatesGoalLease()
    {
        using var _ = EnvVarScope.ForIsolatedDotnetRoot();
        var goalId = new GoalId("decafbaddecafbaddecafbaddecafbad");
        try
        {
            var first = DotnetBuildEnvironmentManager.CreateAttempt(goalId, "test");
            File.WriteAllText(Path.Combine(first.RootPath, "lease", "lease.lock"), "999999");

            var second = DotnetBuildEnvironmentManager.CreateAttempt(goalId, "retry");

            Assert.True(second.StaleLockCleared);
            Assert.Equal(first.ArtifactsPath, second.ArtifactsPath);
            Assert.True(DotnetBuildEnvironmentManager.TryRotateGoalLease(goalId, "corrupt-cache"));
            var third = DotnetBuildEnvironmentManager.CreateAttempt(goalId, "after-rotate");
            Assert.False(third.ReusedGoalLease);
            Assert.Equal(first.LeaseId, third.LeaseId);
            Assert.True(Directory.Exists(Path.Combine(third.RootPath, "rotated-leases")));
        }
        finally
        {
            DotnetBuildEnvironmentManager.TryDeleteGoalArtifacts(goalId);
        }
    }

    [Xunit.Fact(DisplayName = "DotnetBuildEnvironmentManager_inspects_and_cleans_orphaned_goal_lease")]
    public void DotnetBuildEnvironmentManagerInspectsAndCleansOrphanedGoalLease()
    {
        using var envScope = EnvVarScope.ForIsolatedDotnetRoot();
        var goalId = new GoalId("0badcafe0badcafe0badcafe0badcafe");
        try
        {
            var environment = DotnetBuildEnvironmentManager.CreateAttempt(goalId, "test");
            var active = DotnetBuildEnvironmentManager.InspectGoalLease(goalId);
            Assert.Equal(environment.LeaseId, active.LeaseId);
            Assert.True(active.OwnerProcessAlive);
            Assert.False(active.CanCleanup);
            Assert.False(DotnetBuildEnvironmentManager.TryCleanupOrphanedGoalLease(goalId, out _, out var activeDetail));
            Assert.True(activeDetail.Contains("Refusing to delete active build lease", StringComparison.Ordinal));

            using (var metadata = JsonDocument.Parse(File.ReadAllText(environment.LeaseMetadataPath!)))
            {
                var orphanedJson = metadata.RootElement.GetRawText().Replace(
                    $"\"ownerProcessId\": {Environment.ProcessId}",
                    "\"ownerProcessId\": 999999",
                    StringComparison.Ordinal);
                File.WriteAllText(environment.LeaseMetadataPath!, orphanedJson);
            }

            var orphaned = DotnetBuildEnvironmentManager.InspectGoalLease(goalId);
            Assert.False(orphaned.OwnerProcessAlive);
            Assert.True(orphaned.CanCleanup);
            Assert.True(orphaned.Detail.Contains("orphaned", StringComparison.Ordinal));
            Assert.True(DotnetBuildEnvironmentManager.TryCleanupOrphanedGoalLease(goalId, out _, out var cleanupDetail));
            Assert.True(cleanupDetail.Contains("Deleted orphaned build lease", StringComparison.Ordinal));
            Assert.False(Directory.Exists(environment.RootPath));
        }
        finally
        {
            DotnetBuildEnvironmentManager.TryDeleteGoalArtifacts(goalId);
        }
    }

    [Xunit.Fact(DisplayName = "DotnetBuildEnvironmentManager_serializes_same_goal_lease_execution")]
    public async Task DotnetBuildEnvironmentManagerSerializesSameGoalLeaseExecution()
    {
        using var _ = EnvVarScope.ForIsolatedDotnetRoot();
        var goalId = new GoalId("1234abcd1234abcd1234abcd1234abcd");
        try
        {
            var first = DotnetBuildEnvironmentManager.CreateAttempt(goalId, "developer");
            var second = DotnetBuildEnvironmentManager.CreateAttempt(goalId, "tester");
            using var firstLock = DotnetBuildEnvironmentManager.AcquireLeaseExecutionLock(first);
            var secondLockTask = Task.Run(() => DotnetBuildEnvironmentManager.AcquireLeaseExecutionLock(second));
            var earlyWinner = await Task.WhenAny(secondLockTask, Task.Delay(200));
            Xunit.Assert.NotEqual(secondLockTask, earlyWinner);

            firstLock.Dispose();
            using var secondLock = await secondLockTask.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(first.ExecutionLockPath, second.ExecutionLockPath);
        }
        finally
        {
            DotnetBuildEnvironmentManager.TryDeleteGoalArtifacts(goalId);
        }
    }

    [Xunit.Fact(DisplayName = "DotnetBuildEnvironmentManager_stable_slot_build_arguments_are_firewall_covered")]
    public void DotnetBuildEnvironmentManagerStableSlotBuildArgumentsAreFirewallCovered()
    {
        var firewallPaths = DotnetBuildEnvironmentManager.StableSlotTesthostFirewallPaths()
            .Select(path => Path.GetFullPath(path.Path))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        for (var slot = 0; slot < DotnetBuildEnvironmentManager.StableSlotCount; slot++)
        {
            var arguments = DotnetBuildEnvironmentManager.StableSlotBuildArguments(slot);
            var artifactsPath = ArgumentValue(arguments, "--artifacts-path");
            foreach (var project in new[] { "Mcg.AgentOrchestrator.Core.Tests", "Mcg.AgentOrchestrator.Infrastructure.Tests" })
            {
                foreach (var configuration in new[] { "Debug", "Release" })
                {
                    var derivedTesthostPath = Path.GetFullPath(Path.Combine(
                        artifactsPath,
                        "bin",
                        project,
                        $"{configuration.ToLowerInvariant()}_net10.0",
                        "testhost.exe"));

                    Assert.True(firewallPaths.Contains(derivedTesthostPath));
                }
            }
        }
    }

    [Xunit.Fact(DisplayName = "InvokeIsolatedDotnet_passes_disable_build_servers_and_node_reuse_env")]
    public void InvokeIsolatedDotnetPassesDisableBuildServersAndNodeReuseEnv()
    {
        var repoRoot = Environment.GetEnvironmentVariable("MCG_ORCHESTRATOR_REPOSITORY_ROOT") ?? Directory.GetCurrentDirectory();
        var scriptPath = Path.Combine(repoRoot, "scripts", "Invoke-IsolatedDotnet.ps1");
        var script = File.ReadAllText(scriptPath);

        Assert.True(script.Contains("\"--disable-build-servers\"", StringComparison.Ordinal));
        Assert.True(script.Contains("$env:MSBUILDDISABLENODEREUSE = \"1\"", StringComparison.Ordinal));
        Assert.True(script.Contains("$env:DOTNET_CLI_USE_MSBUILD_SERVER = \"0\"", StringComparison.Ordinal));
    }

    private static string ArgumentValue(IReadOnlyList<string> arguments, string name)
    {
        var index = -1;
        for (var i = 0; i < arguments.Count; i++)
        {
            if (arguments[i].Equals(name, StringComparison.Ordinal))
            {
                index = i;
                break;
            }
        }

        Assert.True(index >= 0 && index <= arguments.Count - 2);
        return arguments[index + 1];
    }

    private sealed class EnvVarScope : IDisposable
    {
        private readonly string _name;
        private readonly string? _originalValue;
        private readonly string _root;

        private EnvVarScope(string name, string value)
        {
            _name = name;
            _originalValue = Environment.GetEnvironmentVariable(name);
            _root = value;
            Environment.SetEnvironmentVariable(name, value);
        }

        public static EnvVarScope ForIsolatedDotnetRoot()
        {
            return new EnvVarScope(
                DotnetBuildEnvironmentManager.IsolatedRootOverrideVariable,
                Path.Combine(Path.GetTempPath(), $"{DotnetBuildEnvironmentManager.RootDirectoryName}-test-{Guid.NewGuid():N}"));
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable(_name, _originalValue);
            try
            {
                if (Directory.Exists(_root))
                {
                    Directory.Delete(_root, recursive: true);
                }
            }
            catch
            {
                // Best effort; a failed test may leave a stream open for failure inspection.
            }
        }
    }
}
