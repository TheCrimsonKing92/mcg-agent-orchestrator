using Mcg.AgentOrchestrator.Infrastructure;

public sealed class GoalAcceptanceVerifierTests
{
    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_retries_once_on_CS2012_and_returns_passed")]
    public async Task GoalAcceptanceVerifierRetriesOnceOnCs2012AndReturnsPassed()
    {
        var calls = new List<string[]>();
        var responses = new Queue<GoalAcceptanceVerifier.CommandResult>([
            new(0, ""),                                                     // build-server shutdown
            new(1, "error CS2012: Cannot open 'Core.dll' for writing"),     // dotnet test - CS2012
            new(0, ""),                                                     // build-server shutdown (retry)
            new(0, "Test run succeeded.")                                   // dotnet test - retry passes
        ]);

        var verifier = new GoalAcceptanceVerifier((args, _, _) =>
        {
            calls.Add(args);
            return Task.FromResult(responses.Dequeue());
        });

        var result = await verifier.RunAsync("C:\\fake\\worktree");

        Assert.True(result.Passed);
        Assert.True(result.Retried);
        Assert.True(result.OutputTail is null);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(4, calls.Count);
        Assert.True(calls[2].SequenceEqual(["dotnet", "build-server", "shutdown"]));
        Assert.True(calls[3].SequenceEqual(["dotnet", "test"]));
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_retries_once_on_CS2012_and_returns_failed_when_retry_also_fails")]
    public async Task GoalAcceptanceVerifierRetriesOnceOnCs2012AndReturnsFailedWhenRetryAlsoFails()
    {
        var calls = new List<string[]>();
        var responses = new Queue<GoalAcceptanceVerifier.CommandResult>([
            new(0, ""),
            new(1, "error CS2012: Cannot open 'Core.dll' for writing"),
            new(0, ""),
            new(1, "error CS2012: Cannot open 'Core.dll' for writing -- still locked")
        ]);

        var verifier = new GoalAcceptanceVerifier((args, _, _) =>
        {
            calls.Add(args);
            return Task.FromResult(responses.Dequeue());
        });

        var result = await verifier.RunAsync("C:\\fake\\worktree");

        Assert.False(result.Passed);
        Assert.True(result.Retried);
        Assert.True(result.OutputTail is not null);
        Assert.True(result.OutputTail!.Contains("CS2012", StringComparison.Ordinal));
        Assert.Equal(1, result.ExitCode);
        Assert.Equal(4, calls.Count);
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_does_not_retry_non_CS2012_failure")]
    public async Task GoalAcceptanceVerifierDoesNotRetryNonCs2012Failure()
    {
        var calls = new List<string[]>();
        var responses = new Queue<GoalAcceptanceVerifier.CommandResult>([
            new(0, ""),
            new(1, "Failed 3 tests.\nError: assertion failed")
        ]);

        var verifier = new GoalAcceptanceVerifier((args, _, _) =>
        {
            calls.Add(args);
            return Task.FromResult(responses.Dequeue());
        });

        var result = await verifier.RunAsync("C:\\fake\\worktree");

        Assert.False(result.Passed);
        Assert.False(result.Retried);
        Assert.True(result.OutputTail is not null);
        Assert.Equal(1, result.ExitCode);
        Assert.Equal(2, calls.Count);
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_returns_passed_without_retry_on_first_time_pass")]
    public async Task GoalAcceptanceVerifierReturnsPassedWithoutRetryOnFirstTimePass()
    {
        var calls = new List<string[]>();
        var responses = new Queue<GoalAcceptanceVerifier.CommandResult>([
            new(0, ""),
            new(0, "Test run succeeded.")
        ]);

        var verifier = new GoalAcceptanceVerifier((args, _, _) =>
        {
            calls.Add(args);
            return Task.FromResult(responses.Dequeue());
        });

        var result = await verifier.RunAsync("C:\\fake\\worktree");

        Assert.True(result.Passed);
        Assert.False(result.Retried);
        Assert.True(result.OutputTail is null);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(2, calls.Count);
    }
}
