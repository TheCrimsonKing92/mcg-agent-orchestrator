using System.Text.Json;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

[Collection(TestCollections.GoalAcceptanceVerifier)]
public sealed class GoalAcceptanceVerifierTestsDeclaredToolHomePreflightTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunCheck_RemovedAnsweredHome_FailsBeforeCommandLaunch(bool removeHome)
    {
        using var project = new ProjectOnboardingFixture("no-solution");
        var directory = Path.Combine(project.Root, "tool-home");
        Directory.CreateDirectory(directory);
        var answer = OwnerAnswerValidator.Validate("environment/tool-home/RUSTUP_HOME", directory);
        var settings = AcceptanceGateEngineSettings.Parse(JsonSerializer.Serialize(new
        {
            version = 1, checks = Array.Empty<object>(),
            toolHomes = new[] { new { variable = "RUSTUP_HOME", directory = answer } }
        }));
        Assert.Equal(new DeclaredToolHome("RUSTUP_HOME", directory), Assert.Single(settings.DeclaredToolHomes));
        if (removeHome) Directory.Delete(directory);
        Assert.Equal(!removeHome, Directory.Exists(directory));
        var command = Path.Combine(project.Root, "tool-probe.exe");
        File.WriteAllText(command, "Executed only through the injected command runner.");
        var launches = 0;
        var verifier = new GoalAcceptanceVerifier((_, _, _, _) =>
        {
            launches++;
            return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "probe executed"));
        });
        await using var owner = new AcceptanceAttemptExecutionOwner(
            new AcceptanceAttemptIdentity("tool-home-preflight", "goal", project.Root, "candidate", "main",
                "candidate", Path.Combine(project.Root, "results"), null, Environment.ProcessId, null), settings);
        var check = new GoalAcceptanceVerifier.AcceptanceManifestCheck
        {
            Name = "tool-home-probe", Type = "command-exit", Command = command,
            Arguments = ["--tool-home-probe"], TimeoutMinutes = 1
        };

        var run = await owner.RunInvocationForTestsAsync(
            verifier, owner.CreateInvocation(check.Name), check, project.Root);

        Assert.Equal(!removeHome, run.Result.Passed);
        Assert.Equal(removeHome ? 0 : 1, launches);
        Assert.Equal(check.Name, run.Result.Name);
        if (removeHome)
        {
            Assert.Contains("RUSTUP_HOME", run.Result.OutputTail);
            Assert.Contains(directory, run.Result.OutputTail);
            Assert.Contains("does not exist", run.Result.OutputTail);
            Assert.Null(run.Result.ExitCode);
        }
        else
            Assert.Equal(0, run.Result.ExitCode);
    }
}
