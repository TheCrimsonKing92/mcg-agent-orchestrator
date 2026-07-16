using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class ChaosGateAcceptanceManifestTests : ChaosGateTestBase
{
    // Gate 2: Forbidden-changed-paths guard
    [Xunit.Fact(DisplayName = "ChaosGate2_write_to_forbidden_path_blocks_acceptance")]
    public async System.Threading.Tasks.Task Gate2_WriteToForbiddenPath_BlocksAcceptance()
    {
        var root = CreateManifestWorkspace("""
            {
              "version": 1,
              "checks": [],
              "forbiddenChangedPathGlobs": [".qwen/**", "bin/**"]
            }
            """);
        var responses = new Queue<GoalAcceptanceVerifier.CommandResult>([
            new(0, ""),
            new(0, ".qwen/settings.json\nsrc/safe.cs")
        ]);
        var verifier = new GoalAcceptanceVerifier((args, _, _) =>
            System.Threading.Tasks.Task.FromResult(responses.Dequeue()));

        var result = await verifier.RunAsync(root);

        Assert.False(result.Passed);
        Assert.Equal(1, result.ExitCode);
        var forbidden = result.Checks!.Single(c => c.Name == "forbidden changed paths");
        Assert.False(forbidden.Passed);
        Assert.Contains(".qwen/settings.json", forbidden.OutputTail!, StringComparison.Ordinal);
    }

    // Anti-tautology: weakening Gate 2 (empty globs) lets write through
    [Xunit.Fact(DisplayName = "ChaosGate2_weakened_empty_globs_allow_forbidden_write_proving_gate_is_not_tautological")]
    public async System.Threading.Tasks.Task Gate2_WeakenedEmptyGlobs_AllowForbiddenWrite()
    {
        var root = CreateManifestWorkspace("""
            {
              "version": 1,
              "checks": [],
              "forbiddenChangedPathGlobs": []
            }
            """);
        var responses = new Queue<GoalAcceptanceVerifier.CommandResult>([new(0, "")]);
        var verifier = new GoalAcceptanceVerifier((args, _, _) =>
            System.Threading.Tasks.Task.FromResult(responses.Dequeue()));

        var result = await verifier.RunAsync(root);

        // Weakened gate lets write pass - proves the non-weakened test was non-trivially asserting the gate.
        Assert.True(result.Passed);
        Assert.False(result.Checks!.Any(c => c.Name == "forbidden changed paths" && !c.Passed));
    }
}
