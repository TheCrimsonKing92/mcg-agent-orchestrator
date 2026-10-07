using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: reads only the source in the explicitly verified repository.
public sealed class AcceptanceCheckProcessInvokerOwnershipTests
{
    [Theory]
    [InlineData("private static RegisteredOwnedProcess StartAcceptanceProcess(")]
    [InlineData("private static async Task WriteGateHeartbeatLoopAsync(")]
    [InlineData("private static void ObserveProcessCleanup(")]
    public void Process_members_have_one_owner_outside_the_verifier(string declaration)
    {
        var directory = Path.Combine(VerifiedRepositoryRoot.Find(),
            "src", "Mcg.AgentOrchestrator.Infrastructure", "Workspaces");
        var verifier = File.ReadAllText(Path.Combine(directory, "GoalAcceptanceVerifier.ProcessRunner.cs"));
        var invoker = File.ReadAllText(Path.Combine(directory, "AcceptanceCheckProcessInvoker.cs"));

        Assert.DoesNotContain(declaration, verifier, StringComparison.Ordinal);
        Assert.Contains(declaration, invoker, StringComparison.Ordinal);
        Assert.Contains("internal static class AcceptanceCheckProcessInvoker", invoker, StringComparison.Ordinal);
        Assert.Contains("internal sealed record AcceptanceProcessRequest", invoker, StringComparison.Ordinal);
        Assert.False(Regex.IsMatch(invoker, @"\bpartial\b"));
    }
}
