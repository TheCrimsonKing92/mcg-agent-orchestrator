using System.Diagnostics;
using Mcg.AgentOrchestrator.App.Orchestration;

[Xunit.Collection(TestCollections.EnvMutation)]
public sealed class ConductorSelfRelaunchTests
{
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

    [Xunit.Fact(DisplayName = "ConductorSelfRelaunch_self_check_failure_never_attempts_handoff")]
    public void SelfCheckFailureNeverAttemptsHandoff()
    {
        var root = CreateTempDirectory();
        try
        {
            var handoffCalled = false;
            var result = ConductorSelfRelaunch.TryRelaunch(
                Options(root),
                new ConductorSelfRelaunchRequest("goal1234", 7),
                _ =>
                {
                    using var selfCheck = Process.Start(new ProcessStartInfo("powershell")
                    {
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        ArgumentList =
                        {
                            "-NoProfile",
                            "-Command",
                            "exit 17"
                        }
                    })!;
                    selfCheck.WaitForExit(5000);
                    Assert.Equal(17, selfCheck.ExitCode);
                    throw new ConductorSelfRelaunchPreparationException("self-check", "bad protocol");
                },
                (_, _) =>
                {
                    handoffCalled = true;
                    return ConductorLoopHandoffResult.Skipped("unexpected");
                });

            Assert.False(result.HandedOff);
            Assert.Equal("self-check", result.FailedPhase);
            Assert.False(handoffCalled);
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
            Path.Combine(root, "App.dll.git-head"),
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
}
