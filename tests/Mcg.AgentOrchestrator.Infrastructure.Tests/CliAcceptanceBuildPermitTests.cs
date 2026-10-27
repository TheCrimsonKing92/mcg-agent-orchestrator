using System.Reflection;
using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: leases and lock files belong to each test's unique temporary folders.
public sealed class CliAcceptanceBuildPermitTests
{
    [Fact]
    public void Acquire_ReturnsTheSelectorLeaseAndItsPermitIndex()
    {
        var testFolder = Path.Combine(Path.GetTempPath(), $"cli-build-permit-{Guid.NewGuid():N}");
        var outsideFolder = testFolder + "-outside";
        try
        {
            var storageRoot = new DotnetBuildStorageRoot(testFolder);
            var timeout = TimeSpan.FromMilliseconds(123);

            AssertSelection(testFolder, storageRoot, "build-1", 3, 3);
            AssertSelection(testFolder, storageRoot, "build-4", null, 4);
            AssertSelection(testFolder, storageRoot, "BUILD-2", null, 2);
            Assert.False(storageRoot.ContainsPath(outsideFolder));
            AssertSelection(outsideFolder, null, "build-1", 1, 1);

            void AssertSelection(
                string folder,
                DotnetBuildStorageRoot? configuredRoot,
                string token,
                int? permitIndex,
                int expectedIndex)
            {
                using var selectorLease = CreateLease(folder, token, permitIndex);
                TimeSpan? receivedTimeout = null;
                Action<DotnetBuildStableSlotWait>? receivedWait = null;
                var result = CliAcceptanceBuildPermit.Acquire(
                    GoalId.New(),
                    (selectedTimeout, onWait) =>
                    {
                        receivedTimeout = selectedTimeout;
                        receivedWait = onWait;
                        return selectorLease;
                    },
                    timeout,
                    configuredRoot,
                    testFolder);
                using var acquiredLease = result.Lease;

                Assert.Same(selectorLease, result.Lease);
                Assert.Equal(expectedIndex, result.SlotIndex);
                Assert.True(result.Lease.IsExecutionLockHeld);
                Assert.Equal(timeout, receivedTimeout);
                Assert.NotNull(receivedWait);
            }
        }
        finally
        {
            if (Directory.Exists(testFolder))
                Directory.Delete(testFolder, recursive: true);
            if (Directory.Exists(outsideFolder))
                Directory.Delete(outsideFolder, recursive: true);
        }
    }

    [Fact]
    public void Acquire_RejectsAndReleasesUnusableLeases()
    {
        var testFolder = Path.Combine(Path.GetTempPath(), $"cli-build-permit-{Guid.NewGuid():N}");
        try
        {
            var storageRoot = new DotnetBuildStorageRoot(Path.Combine(testFolder, "intended"));
            using var outsideLease = CreateLease(Path.Combine(testFolder, "other"), "build-1", 1);
            Assert.True(outsideLease.IsExecutionLockHeld);
            var namespaceError = Assert.Throws<InvalidOperationException>(() =>
                CliAcceptanceBuildPermit.Acquire(
                    GoalId.New(), (_, _) => outsideLease, null, storageRoot, testFolder));
            Assert.Equal("Acceptance build lease is outside the configured build storage namespace.", namespaceError.Message);
            Assert.False(outsideLease.IsExecutionLockHeld);

            using var missingIndexLease = CreateLease(storageRoot.RootPath, "build-x", null);
            Assert.True(missingIndexLease.IsExecutionLockHeld);
            var indexError = Assert.Throws<IOException>(() =>
                CliAcceptanceBuildPermit.Acquire(
                    GoalId.New(), (_, _) => missingIndexLease, null, storageRoot, testFolder));
            Assert.Equal("Build permit did not identify its pool index: build-x", indexError.Message);
            Assert.False(missingIndexLease.IsExecutionLockHeld);

            var selectorError = new IOException("selector failed");
            var propagatedError = Assert.Throws<IOException>(() =>
                CliAcceptanceBuildPermit.Acquire(
                    GoalId.New(), (_, _) => throw selectorError, null, storageRoot, testFolder));
            Assert.Same(selectorError, propagatedError);
        }
        finally
        {
            if (Directory.Exists(testFolder))
                Directory.Delete(testFolder, recursive: true);
        }
    }

    [Fact]
    public void FormatBusySlots_KeepsTheCliFormat()
    {
        Assert.Equal(
            "slot-1:pid-42|slot-2:pid-unknown",
            CliAcceptanceBuildPermit.FormatBusySlots(
                [new DotnetBuildStableSlotWait(1, 42), new DotnetBuildStableSlotWait(2, null)]));
        Assert.Equal(string.Empty, CliAcceptanceBuildPermit.FormatBusySlots([]));
    }

    [Fact]
    public void BuildPermitSelection_HasOneOwner()
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic |
            BindingFlags.Static | BindingFlags.DeclaredOnly;
        var handlerMembers = typeof(CliCommandHandlers).GetMembers(flags);
        foreach (var name in new[] { "SelectGoalBuildPermit", "ParseStableSlotIndex", "FormatBusySlots" })
            Assert.DoesNotContain(handlerMembers, member => member.Name == name);

        var owner = typeof(CliAcceptanceBuildPermit);
        Assert.True(owner.IsAbstract);
        Assert.True(owner.IsSealed);
        var ownerMethods = owner.GetMethods(flags);
        foreach (var name in new[] { "Acquire", "SelectGoalBuildPermit", "ParseStableSlotIndex", "FormatBusySlots" })
            Assert.Single(ownerMethods.Where(method => method.Name == name));
        Assert.True(Assert.Single(ownerMethods.Where(method => method.Name == "Acquire")).IsAssembly);
        Assert.True(Assert.Single(ownerMethods.Where(method => method.Name == "FormatBusySlots")).IsAssembly);

        var cliDirectory = Path.Combine(VerifiedRepositoryRoot.Find(), "src", "Mcg.AgentOrchestrator.App", "Cli");
        var handlerSource = File.ReadAllText(Path.Combine(cliDirectory, "CliCommandHandlers.Goals.Acceptance.cs"));
        Assert.Equal(1, Regex.Matches(handlerSource, Regex.Escape("CliAcceptanceBuildPermit.Acquire(")).Count);
        Assert.DoesNotContain("DotnetBuildEnvironmentManager.", handlerSource);
        var ownerSource = File.ReadAllText(Path.Combine(cliDirectory, "CliAcceptanceBuildPermit.cs"));
        Assert.DoesNotContain("partial class", ownerSource);

        Assert.Contains(handlerMembers, member => member.Name == "FormatBuildLockBlocked");
        Assert.Contains(handlerMembers, member => member.Name == "EscapeBlockerDetail");
    }

    private static DotnetBuildEnvironmentLease CreateLease(string folder, string token, int? permitIndex)
    {
        var leaseRoot = Path.Combine(folder, ".fake-build-slot");
        Directory.CreateDirectory(leaseRoot);
        var lockPath = Path.Combine(leaseRoot, $"{Guid.NewGuid():N}.lock");
        var stream = new FileStream(lockPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        var environment = new DotnetBuildEnvironment(
            "fake-acceptance-test-slot",
            leaseRoot,
            Path.Combine(leaseRoot, "artifacts"),
            lockPath,
            [],
            token,
            BuildPermitIndex: permitIndex);
        return new DotnetBuildEnvironmentLease(environment, stream);
    }
}
