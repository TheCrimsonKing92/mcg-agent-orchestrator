using System.ComponentModel;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;

[Collection(TestCollections.DotnetBuildSlots)]
[Trait("Category", "LocalHostOnly")]
public sealed class DotnetBuildEnvironmentManagerTestsLocalHostOnlyLoopHandoffBreakawayFallback(ITestOutputHelper output)
    : ConductorBatchLoopTests(output)
{
    public static bool IsWindows => OperatingSystem.IsWindows();
    private const uint Breakaway = 0x01000000;

    [Fact(Skip = "Requires Windows native process creation.", SkipUnless = nameof(IsWindows))]
    public void BreakawayDeniedRetriesOnceWithoutBreakaway()
    {
        var root = CreateTempDirectory("mcg-breakaway-fallback");
        ConductorSupervisorProcessIdentity? successor = null;
        try
        {
            var request = Request(root);
            var flagsSeen = new List<uint>();
            var createdPid = 0;
            var result = ConductorLoopHandoff.LaunchDetachedWindows(
                request, Command(),
                acquireConsoleSuppression: NoConsoleSuppression,
                createProcess: (flags, createNative) =>
                {
                    flagsSeen.Add(flags);
                    if ((flags & Breakaway) != 0)
                        return new(false, IntPtr.Zero, IntPtr.Zero, 0, 5);
                    var outcome = createNative(flags);
                    createdPid = outcome.ProcessId;
                    return outcome;
                });
            successor = result.SuccessorIdentity;
            Assert.True(createdPid > 0);
            Assert.Equal(createdPid, result.ProcessId);
            Assert.Equal(2, flagsSeen.Count);
            Assert.NotEqual(0u, flagsSeen[0] & Breakaway);
            Assert.Equal(flagsSeen[0] & ~Breakaway, flagsSeen[1]);
            Assert.Contains("breakawaySucceeded=false", result.LaunchDetail, StringComparison.Ordinal);
            Assert.True(File.Exists(request.StdoutPath));
            Assert.True(File.Exists(request.StderrPath));

            var lines = AuthorityLines(root);
            Assert.Equal(2, lines.Length);
            using var first = AuthorityDetail(lines[0]);
            using var second = AuthorityDetail(lines[1]);
            Assert.Equal(5, first.RootElement.GetProperty("nativeError").GetInt32());
            Assert.False(first.RootElement.TryGetProperty("fallback", out _));
            Assert.Equal(0, second.RootElement.GetProperty("nativeError").GetInt32());
            Assert.Equal(result.ProcessId, second.RootElement.GetProperty("successorPid").GetInt32());
            Assert.Equal("inherit-job", second.RootElement.GetProperty("fallback").GetString());
            Assert.Contains("fallback=inherit-job", lines[1], StringComparison.Ordinal);
            Assert.Equal(flagsSeen[0], Flags(first));
            Assert.Equal(flagsSeen[1], Flags(second));
        }
        finally
        {
            TestOwnedProcessStop.StopTreeIfSame(successor);
            TryDeleteDirectory(root);
        }
    }

    [Fact(Skip = "Requires Windows native process creation.", SkipUnless = nameof(IsWindows))]
    public void OtherNativeErrorsAreNotRetried() => AssertFailedLaunch(2, 1);

    [Fact(Skip = "Requires Windows native process creation.", SkipUnless = nameof(IsWindows))]
    public void DeniedFallbackFailureRemovesOutputFiles() => AssertFailedLaunch(5, 2);

    private void AssertFailedLaunch(int nativeError, int expectedAttempts)
    {
        var root = CreateTempDirectory("mcg-breakaway-failure");
        try
        {
            var request = Request(root);
            var calls = 0;
            var error = Assert.Throws<Win32Exception>(() => ConductorLoopHandoff.LaunchDetachedWindows(
                request, Command(),
                acquireConsoleSuppression: NoConsoleSuppression,
                createProcess: (_, _) =>
                {
                    calls++;
                    return new(false, IntPtr.Zero, IntPtr.Zero, 0, nativeError);
                }));
            Assert.Equal(expectedAttempts, calls);
            Assert.Equal(nativeError, error.NativeErrorCode);
            Assert.Contains(request.Name, error.Message, StringComparison.Ordinal);
            Assert.False(File.Exists(request.StdoutPath));
            Assert.False(File.Exists(request.StderrPath));
            Assert.Equal(expectedAttempts, AuthorityLines(root).Length);
        }
        finally { TryDeleteDirectory(root); }
    }

    [Theory]
    [InlineData(0x01080600u, 5, true)]
    [InlineData(0x00080600u, 5, false)]
    [InlineData(0x01080600u, 2, false)]
    public void RetryRequiresBreakawayAndAccessDenied(uint flags, int error, bool expected)
    {
        Assert.Equal(expected, DetachedLaunchBreakawayFallback.TryGetRetryFlags(flags, error, out var retry));
        if (expected) Assert.Equal(flags & ~Breakaway, retry);
    }

    private static ProcessTreeGuiSuppression.ConsoleSpawnScope NoConsoleSuppression() =>
        new(childConsolePolicyApplied: false, onDispose: () => { });

    private static ConductLoopLaunchRequest Request(string root) =>
        new("fallback-probe-" + Guid.NewGuid().ToString("N"), [],
            Path.Combine(root, "successor.out.log"), Path.Combine(root, "successor.err.log"), root, 0);

    private static string[] Command() =>
        [Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"), "/d", "/c", "exit", "0"];

    private static string[] AuthorityLines(string root) =>
        File.ReadAllLines(Path.Combine(root, ConductEventLogWriter.CurrentFileName))
            .Where(line => line.Contains("\"eventKind\":\"loop-handoff-launch-authority\"", StringComparison.Ordinal)).ToArray();

    private static JsonDocument AuthorityDetail(string line)
    {
        using var entry = JsonDocument.Parse(line);
        return JsonDocument.Parse(entry.RootElement.GetProperty("detail").GetString()!);
    }

    private static uint Flags(JsonDocument detail) =>
        Convert.ToUInt32(detail.RootElement.GetProperty("authority").GetProperty("creationFlags").GetString()![2..], 16);
}
