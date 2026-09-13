using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Extensions.Configuration;

public sealed class WorktreeCleanupConfigurationTests
{
    [Xunit.Fact(DisplayName = "WorktreeCleanup_configuration_reads_all_values")]
    public void WorktreeCleanupConfigurationReadsAllValues()
    {
        var section = BuildSection(new Dictionary<string, string?>
        {
            ["WorktreeCleanup:SweepInterval"] = "00:07:00",
            ["WorktreeCleanup:EscalationThreshold"] = "5",
            ["WorktreeCleanup:EscalatedRetryInterval"] = "2.00:00:00"
        });

        var options = WorktreeCleanupConfiguration.Read(section);

        Assert.Equal(TimeSpan.FromMinutes(7), options.SweepInterval);
        Assert.Equal(5, options.EscalationThreshold);
        Assert.Equal(TimeSpan.FromDays(2), options.EscalatedRetryInterval);
    }

    [Xunit.Theory(DisplayName = "WorktreeCleanup_configuration_rejects_malformed_values")]
    [Xunit.InlineData("SweepInterval", "not-a-timespan", "must be a TimeSpan")]
    [Xunit.InlineData("EscalationThreshold", "not-an-integer", "must be an integer")]
    [Xunit.InlineData("EscalatedRetryInterval", "not-a-timespan", "must be a TimeSpan")]
    public void WorktreeCleanupConfigurationRejectsMalformedValues(
        string key,
        string value,
        string expectedMessage)
    {
        var section = BuildSection(new Dictionary<string, string?>
        {
            [$"WorktreeCleanup:{key}"] = value
        });

        var exception = Assert.Throws<InvalidOperationException>(() => WorktreeCleanupConfiguration.Read(section));

        Assert.Contains(expectedMessage, exception.Message, StringComparison.Ordinal);
    }

    [Xunit.Theory(DisplayName = "WorktreeCleanup_options_reject_nonpositive_values")]
    [Xunit.InlineData(0, 3, 1, "SweepInterval")]
    [Xunit.InlineData(1, 0, 1, "EscalationThreshold")]
    [Xunit.InlineData(1, 3, 0, "EscalatedRetryInterval")]
    public void WorktreeCleanupOptionsRejectNonpositiveValues(
        int sweepMinutes,
        int escalationThreshold,
        int escalatedRetryDays,
        string parameterName)
    {
        var options = new GoalWorktreeCleanupOptions(
            TimeSpan.FromMinutes(sweepMinutes),
            escalationThreshold,
            TimeSpan.FromDays(escalatedRetryDays));

        var exception = Assert.Throws<ArgumentOutOfRangeException>(() => options.Validate());

        Assert.Equal(parameterName, exception.ParamName);
    }

    [Xunit.Fact(DisplayName = "GoalWorktree_cleanup_hooks_for_configuration_fix_options_and_normalize_attention_directory")]
    public void GoalWorktreeCleanupHooksForConfigurationFixOptionsAndNormalizeAttentionDirectory()
    {
        var options = new GoalWorktreeCleanupOptions(
            TimeSpan.FromMinutes(7),
            5,
            TimeSpan.FromDays(2));
        var relativeAttentionDirectory = Path.Combine("cleanup-attention", Guid.NewGuid().ToString("N"));

        var hooks = GoalWorktreeCleanupHooks.ForConfiguration(options, relativeAttentionDirectory);

        Assert.Same(options, hooks.CleanupOptions());
        Assert.Equal(Path.GetFullPath(relativeAttentionDirectory), hooks.CleanupAttentionStoreDirectory());
    }

    [Xunit.Fact(DisplayName = "GoalWorktree_cleanup_hooks_for_configuration_reject_invalid_options")]
    public void GoalWorktreeCleanupHooksForConfigurationRejectInvalidOptions()
    {
        var options = new GoalWorktreeCleanupOptions(
            TimeSpan.Zero,
            1,
            TimeSpan.FromDays(1));

        var exception = Assert.Throws<ArgumentOutOfRangeException>(
            () => GoalWorktreeCleanupHooks.ForConfiguration(options));

        Assert.Equal("SweepInterval", exception.ParamName);
    }

    private static IConfiguration BuildSection(IReadOnlyDictionary<string, string?> values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build()
            .GetSection("WorktreeCleanup");
}
