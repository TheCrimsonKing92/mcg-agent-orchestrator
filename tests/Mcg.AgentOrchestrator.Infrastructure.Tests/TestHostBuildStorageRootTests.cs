using System.Diagnostics;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

namespace CliCommandTestsBuildStorageRoot;

// Serial collection: the two scenario facts temporarily change the process storage root.
[Xunit.Collection(TestCollections.IsolatedDotnetRoot)]
public sealed class TestHostBuildStorageRootTests : ConductorBatchLoopTests
{
    private const string PreservationReceiptVariable = "MCG_TEST_BUILD_STORAGE_ROOT_RECEIPT";
    private const string RootVariable = DotnetBuildEnvironmentManager.IsolatedRootOverrideVariable;

    public TestHostBuildStorageRootTests(ITestOutputHelper output) : base(output) { }

    [Fact]
    public void ModuleBaselineRootIsNotTheDefaultRoot()
    {
        var baseline = AssemblyDotnetBuildStorageRootIsolation.BaselineRoot;
        Assert.NotNull(baseline);
        Assert.True(Directory.Exists(baseline), $"Missing module baseline directory: {baseline}");
        var fullBaseline = Path.GetFullPath(baseline).TrimEnd('\\', '/');
        var defaultRoot = AssemblyDotnetBuildStorageRootIsolation.DefaultRoot().TrimEnd('\\', '/');
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        Assert.False(string.Equals(fullBaseline, defaultRoot, comparison));
        Assert.False(fullBaseline.StartsWith(defaultRoot + Path.DirectorySeparatorChar, comparison));
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("   ", true)]
    [InlineData(@"C:\repo\LocalLow\mcg-dotnet-isolated", true)]
    [InlineData(@"C:\repo\LocalLow\mcg-dotnet-isolated\", true)]
    [InlineData(@"C:\repo\project\dotnet", false)]
    public void ShouldReplaceOnlyAnUnsetOrDefaultInheritedRoot(string? inherited, bool expected)
    {
        Assert.Equal(expected, AssemblyDotnetBuildStorageRootIsolation.ShouldReplace(
            inherited, @"C:\repo\LocalLow\mcg-dotnet-isolated"));
    }

    [Fact(Timeout = TestHangGuard.WholeTestMilliseconds)]
    public void ProbeShapedTickWritesItsGoalFolderUnderTheBaselineRootOnly()
    {
        var prior = Environment.GetEnvironmentVariable(RootVariable);
        Assert.False(string.IsNullOrWhiteSpace(prior), "The process baseline must exist before environment mutation.");
        var baseline = AssemblyDotnetBuildStorageRootIsolation.BaselineRoot;
        Assert.False(string.IsNullOrWhiteSpace(baseline), "Module baseline initialization failed.");
        var root = CreateTempDirectory("mcg-baseline-probe");
        try
        {
            Environment.SetEnvironmentVariable(RootVariable, baseline);
            RunProbeShapedTick(root, baseline, (goalFolder, defaultGoalFolder) =>
            {
                Assert.True(Directory.Exists(goalFolder));
                Assert.False(Directory.Exists(defaultGoalFolder));
                Assert.True(File.Exists(Path.Combine(goalFolder, "lease", "lease.json")));
            });
        }
        finally
        {
            Environment.SetEnvironmentVariable(RootVariable, prior);
            TryDeleteDirectory(root);
        }
    }

    [Fact(Timeout = TestHangGuard.WholeTestMilliseconds)]
    public void ProbeShapedTickWritesWhereverTheVariablePoints()
    {
        var prior = Environment.GetEnvironmentVariable(RootVariable);
        Assert.False(string.IsNullOrWhiteSpace(prior), "The process baseline must exist before environment mutation.");
        var root = CreateTempDirectory("mcg-configured-probe");
        var storageRoot = Path.Combine(root, "test-dotnet");
        try
        {
            Environment.SetEnvironmentVariable(RootVariable, storageRoot);
            RunProbeShapedTick(root, storageRoot, (goalFolder, _) => Assert.True(Directory.Exists(goalFolder)));
        }
        finally
        {
            Environment.SetEnvironmentVariable(RootVariable, prior);
            TryDeleteDirectory(root);
        }
    }

    [Fact(Timeout = TestHangGuard.WholeTestMilliseconds)]
    public void InheritedNonDefaultRootIsKeptByAFreshProcess()
    {
        var receipt = Environment.GetEnvironmentVariable(PreservationReceiptVariable);
        if (!string.IsNullOrWhiteSpace(receipt))
        {
            // BaselineRoot is the environment value captured immediately after initialization,
            // before this collection's fixture temporarily replaces the live variable.
            Assert.False(string.IsNullOrWhiteSpace(AssemblyDotnetBuildStorageRootIsolation.BaselineRoot));
            File.WriteAllText(receipt, AssemblyDotnetBuildStorageRootIsolation.BaselineRoot);
            return;
        }

        var root = CreateTempDirectory("mcg-inherited-build-root");
        try
        {
            var configuredRoot = Path.Combine(root, "configured-root");
            receipt = Path.Combine(root, "baseline.txt");
            var startInfo = new ProcessStartInfo
            {
                FileName = ResolveDotnetHostPath(),
                WorkingDirectory = root,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add(typeof(TestHostBuildStorageRootTests).Assembly.Location);
            startInfo.ArgumentList.Add("--filter-method");
            startInfo.ArgumentList.Add($"*{nameof(TestHostBuildStorageRootTests)}.{nameof(InheritedNonDefaultRootIsKeptByAFreshProcess)}");
            startInfo.ArgumentList.Add("--results-directory");
            startInfo.ArgumentList.Add(Path.Combine(root, "results"));
            startInfo.Environment[RootVariable] = configuredRoot;
            startInfo.Environment[PreservationReceiptVariable] = receipt;

            var result = CliChildProcessRunner.Run(startInfo, TestHangGuard.HangSafetyBound);
            Assert.True(result.ExitCode == 0,
                $"Inherited-root child failed ({result.ExitCode}). stdout={result.StandardOutput} stderr={result.StandardError}");
            Assert.True(File.Exists(receipt), "Inherited-root child did not execute the selected fact and publish its baseline.");
            Assert.Equal(configuredRoot, File.ReadAllText(receipt));
        }
        finally { TryDeleteDirectory(root); }
    }

    private static void RunProbeShapedTick(string root, string storageRoot, Action<string, string> verify)
    {
        var defaultRoot = new DotnetBuildStorageRoot(AssemblyDotnetBuildStorageRootIsolation.DefaultRoot());
        var configuredRoot = new DotnetBuildStorageRoot(storageRoot);
        Assert.False(AssemblyDotnetBuildStorageRootIsolation.ShouldReplace(storageRoot, defaultRoot.RootPath));
        for (var attempt = 0; attempt < 10; attempt++)
        {
            var kernel = new AgentOrchestratorKernel();
            var goal = CreateVerifiedSimpleGoal(kernel, "Update src/Probe.cs");
            var goalFolder = DotnetBuildEnvironmentManager.GoalRoot(goal.Id, configuredRoot);
            var defaultGoalFolder = DotnetBuildEnvironmentManager.GoalRoot(goal.Id, defaultRoot);
            // Never prepare an absence assertion by deleting another goal's storage.
            if (Directory.Exists(defaultGoalFolder) || Directory.Exists(goalFolder))
                continue;

            try
            {
                var now = new DateTimeOffset(2026, 10, 25, 9, 20, 44, TimeSpan.Zero);
                var gateCalls = 0;
                var coordinator = new ConductorParallelAcceptanceAttemptCoordinator(
                    root, TrunkBranchName.Default, runInline: true, utcNow: () => now);
                var driver = MakeDriver(
                    getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
                    runAcceptanceWithSlot: (_, _) =>
                    {
                        gateCalls++;
                        throw new InvalidOperationException(WorkspaceProbeFaultClassifierTests.F1);
                    },
                    getLandingFileScopes: _ => ["src/Probe.cs"],
                    parallelAcceptanceAttemptCoordinator: coordinator,
                    utcNow: () => now,
                    retryTask: (goalId, taskId, reason) => kernel.RetryTask(goalId, taskId, reason),
                    recordCriterionRetryFeedback: kernel.RecordCriterionRetryFeedback);
                var summary = new ConductorBatchLoop().Run(kernel, driver, ConductorAutonomyPolicy.Conservative,
                    NoStopPath(), maxIterations: 1);
                Assert.Equal(1, summary.Held);
                Assert.Equal(1, gateCalls);
                verify(goalFolder, defaultGoalFolder);
                return;
            }
            finally { TryDeleteDirectory(goalFolder); }
        }

        Assert.Fail("Could not allocate an unused goal prefix for the storage-root probe.");
    }
}
