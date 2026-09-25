using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.Infrastructure.Tests;

public sealed class OwnedRunRootWriteFailureBufferTests
{
    [Xunit.Fact]
    public void Scoped_drains_remove_only_their_roots_and_unscoped_drain_is_destructive()
    {
        var buffer = new OwnedRunRootWriteFailureBuffer();
        buffer.Enqueue("root-a", "failure-a");
        buffer.Enqueue("root-b", "failure-b");

        Assert.Equal(["failure-a"], buffer.Drain(25, "root-a"));
        Assert.Equal(["failure-b"], buffer.Drain(25, "root-b"));
        Assert.Empty(buffer.Drain(25, null));

        buffer.Enqueue("root-c", "failure-c");
        Assert.Equal(["failure-c"], buffer.Drain(25, null));
        Assert.Empty(buffer.Drain(25, null));

        buffer.Enqueue("root-a", "first");
        buffer.Enqueue("root-b", "second");
        buffer.Enqueue("root-a", "third");
        Assert.Equal(["first", "second", "third"], buffer.Drain(25, null));
    }
}
