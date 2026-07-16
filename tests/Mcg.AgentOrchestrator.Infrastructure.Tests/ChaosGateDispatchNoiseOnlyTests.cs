using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class ChaosGateDispatchNoiseOnlyTests : ChaosGateTestBase
{
    // Gate 5: Noise-only commit (.qwen/settings.json)
    [Xunit.Fact(DisplayName = "ChaosGate5_noise_only_qwen_settings_commit_is_rejected")]
    public void Gate5_NoiseOnlyQwenSettingsCommit_IsRejected()
    {
        var root = CreateSeededRepo();
        var (kernel, goal, task, _) = CreateChaosDispatch(
            root, AgentRole.Developer,
            WorkerResultBlock("none", "none", "not run"),
            string.Empty,
            mutateWorktree: wt =>
            {
                var qwenDir = Path.Combine(wt, ".qwen");
                Directory.CreateDirectory(qwenDir);
                File.WriteAllText(Path.Combine(qwenDir, "settings.json"), "{}");
                RunGit(wt, ["add", "-A"], CommittedAt);
                RunGit(wt, ["commit", "-m", "Qwen settings noise"], CommittedAt);
            });

        new BackgroundDispatchRunner(isStillRunning: _ => false)
            .RefreshLatestProcess(kernel, goal.Id, task.Id);

        Assert.Equal(WorkTaskStatus.Failed, task.Status);
        Assert.Equal(1, task.LastVerification!.ExitCode);
        Assert.Contains(
            "did not produce required relevant file-change evidence",
            task.LastVerification.StandardError,
            StringComparison.Ordinal);
    }
}
