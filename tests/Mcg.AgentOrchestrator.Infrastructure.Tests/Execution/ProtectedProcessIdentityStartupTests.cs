using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.Infrastructure.Tests;

public sealed class ProtectedProcessIdentityStartupTests
{
    [Theory]
    [InlineData("4001", null, null)]
    [InlineData("4001", "100", null)]
    [InlineData("4001", "100", 200L)]
    public void InvalidInheritedIdentityIsReplaced(string pid, string? ticks, long? liveTicks)
    {
        var current = new ProtectedProcessIdentity(8001, 300);
        Assert.Equal(current, ProtectedProcessIdentity.ResolveAtStartup(pid, ticks, current, _ => liveTicks));
    }

    [Fact]
    public void MatchingLiveInheritedIdentityIsKept()
    {
        var inherited = new ProtectedProcessIdentity(4001, 100);
        Assert.Equal(inherited, ProtectedProcessIdentity.ResolveAtStartup("4001", "100",
            new ProtectedProcessIdentity(8001, 300), _ => 100));
    }
}
