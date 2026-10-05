using Mcg.AgentOrchestrator.App.Orchestration;

// Parallel-safe: pure classification without shared state.
public sealed class ConductEventOperatorClassifierTestsSandboxHealth
{
    [Theory]
    [InlineData("HOST_HEALTH_WORKER_SANDBOX_OFF", "decision")]
    [InlineData("HOST_HEALTH_WORKER_SANDBOX_ON", "outcome")]
    [InlineData("HOST_HEALTH_REPOSITORY_LOW_WRITABLE", "decision")]
    [InlineData("HOST_HEALTH_REPOSITORY_LOW_WRITABLE_CLEARED", "outcome")]
    public void RequiresWholeEventNameAndHostHealthKind(string name, string expected)
    {
        Assert.Equal(expected, ConductEventOperatorClassifier.Classify("host-health", name));
        Assert.Equal(expected, ConductEventOperatorClassifier.Classify("host-health", name + " detail=value"));
        Assert.Null(ConductEventOperatorClassifier.Classify("host-health", name + "_EXTRA"));
        Assert.Null(ConductEventOperatorClassifier.Classify("other", name));
        Assert.Null(ConductEventOperatorClassifier.Classify("other", name + " detail=value"));
    }
}
