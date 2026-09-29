using System.Reflection;

public sealed class SeedRepositoryContainerNameTests
{
    [Xunit.Fact]
    public void ProcessGenerationsDoNotReuseContainerNamesWhenCounterRestarts()
    {
        var firstGeneration = BuildSeedRepositoryContainerName(processStartTimeUtcTicks: 1, sequence: 1);
        var secondGeneration = BuildSeedRepositoryContainerName(processStartTimeUtcTicks: 2, sequence: 1);

        Assert.NotEqual(firstGeneration, secondGeneration);
    }

    private static string BuildSeedRepositoryContainerName(long processStartTimeUtcTicks, int sequence)
    {
        var method = typeof(GoalWorktreeTestBase).GetMethod(
            "BuildSeedRepositoryContainerName",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return (string)method.Invoke(null, [processStartTimeUtcTicks, sequence])!;
    }
}
