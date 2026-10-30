// Parallel-safe: rendering uses immutable snapshots and no external resources.
using Mcg.AgentOrchestrator.App.OwnerConsole;

public sealed class OwnerConsoleGateDisabledExecutorWhereTests
{
    [Theory]
    [InlineData(80, false)]
    [InlineData(160, false)]
    [InlineData(80, true)]
    [InlineData(160, true)]
    public void Lines_DisabledExecutor_ShowsDisabledInsteadOfSlotCapacity(int width, bool running)
    {
        var state = running ? OwnerConsoleGateLaneState.Running : OwnerConsoleGateLaneState.Queued;
        var row = new OwnerConsoleGateLane("A", state, "unknown", "mcg-exec-2", TimeSpan.Zero, 60, true);
        var gate = new OwnerConsoleGateProgress(true, "cohort", [], null, TimeSpan.Zero,
            [row], 0, 60, 0, 1, Capacity: new([new("mcg-exec-1", 3), new("mcg-exec-2", 2, false)], 5));

        var lines = OwnerConsoleGateLayout.Lines(gate, width, expanded: false);
        var whereIndex = Array.FindIndex(lines.ToArray(), line => line.StartsWith("WHERE   ", StringComparison.Ordinal));
        Assert.True(whereIndex >= 0, "WHERE line was not rendered");
        var where = string.Join(" ", lines.Skip(whereIndex));

        Assert.Contains("mcg-exec-1 0/3 [----------]", where);
        Assert.Contains("mcg-exec-2 disabled", where);
        Assert.DoesNotContain("/2", where);
        Assert.DoesNotContain("mcg-exec-2 1 running", where);
        Assert.Contains("local 0/5 [----------]", where);
        Assert.All(lines, line => Assert.True(line.Length <= width));
    }
}
