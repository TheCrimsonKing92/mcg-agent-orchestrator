using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;

public sealed class CliContextUsageCommandTests
{
    [Fact]
    public void FiltersRoleAndInclusiveOffsetTimestamp()
    {
        var goal = ContextUsageTestFixture.CreateGoal();
        using var output = new StringWriter();
        using var error = new StringWriter();
        var code = CliContextUsageCommand.Run(
            ["context-usage", "--role", "tester", "--since", "2026-09-15T07:07:00-05:00"],
            () => [goal], output, error);

        Assert.Equal(0, code);
        Assert.Equal(string.Empty, error.ToString());
        Assert.Contains("Tester | gpt-5 | dispatches=2 reported-input=2", output.ToString());
        Assert.DoesNotContain("Developer |", output.ToString());

        output.GetStringBuilder().Clear();
        code = CliContextUsageCommand.Run(["context-usage", "--role", "researcher"],
            () => [goal], output, error);
        Assert.Equal(0, code);
        Assert.Contains("rows=0", output.ToString());
    }

    [Fact]
    public void JsonMatchesComputedFiguresAndKeepsUnavailableStatisticsNull()
    {
        var goal = ContextUsageTestFixture.CreateGoal();
        using var output = new StringWriter();
        using var error = new StringWriter();
        var code = CliContextUsageCommand.Run(["context-usage", "--json"],
            () => [goal], output, error);
        Assert.Equal(0, code);
        using var document = JsonDocument.Parse(output.ToString());
        var rows = document.RootElement.GetProperty("rows");
        Assert.Equal(2, rows.GetArrayLength());
        var developer = rows.EnumerateArray().Single(row => row.GetProperty("role").GetString() == "Developer");
        Assert.Equal(6, developer.GetProperty("dispatchCount").GetInt32());
        Assert.Equal(4, developer.GetProperty("reportedInputCount").GetInt32());
        Assert.Equal(25000, developer.GetProperty("input").GetProperty("median").GetDouble());
        Assert.Equal(40000, developer.GetProperty("input").GetProperty("p90").GetInt64());
        Assert.Equal(15000, developer.GetProperty("uncached").GetProperty("median").GetDouble());
        Assert.Equal(20000, developer.GetProperty("harnessOverhead").GetProperty("median").GetDouble());

        output.GetStringBuilder().Clear();
        code = CliContextUsageCommand.Run(
            ["context-usage", "--since", "2026-09-15T12:04:00Z", "--role", "Developer", "--json"],
            () => [goal], output, error);
        Assert.Equal(0, code);
        using var filtered = JsonDocument.Parse(output.ToString());
        var row = Assert.Single(filtered.RootElement.GetProperty("rows").EnumerateArray());
        Assert.Equal(2, row.GetProperty("dispatchCount").GetInt32());
        Assert.Equal(0, row.GetProperty("reportedInputCount").GetInt32());
        Assert.Equal(JsonValueKind.Null, row.GetProperty("input").GetProperty("median").ValueKind);
        Assert.Equal(JsonValueKind.Null, row.GetProperty("harnessOverhead").GetProperty("p90").ValueKind);
    }

    [Fact]
    public void ClassifiesQueryOnlyAndRejectsUnknownOrMalformedFlagsBeforeLoading()
    {
        Assert.Equal(CliCommandCapability.QueryOnly,
            CliCommandCapabilities.Classify(["context-usage"]));
        var exception = Assert.Throws<ArgumentException>(() =>
            CliCommandHelp.ThrowIfInvalidFlags(["context-usage", "--bogus"]));
        Assert.Contains(CliCommandHelp.ContextUsageUsage, exception.Message);

        var loads = 0;
        using var output = new StringWriter();
        using var error = new StringWriter();
        foreach (var args in new string[][] {
            ["context-usage", "--bogus"],
            ["context-usage", "--since", "2026-09-15"],
            ["context-usage", "--role"] })
        {
            Assert.Equal(1, CliContextUsageCommand.Run(args, () =>
            {
                loads++;
                return [ContextUsageTestFixture.CreateGoal()];
            }, output, error));
        }
        Assert.Equal(0, loads);
        Assert.Contains(CliCommandHelp.ContextUsageUsage, error.ToString());
    }
}
