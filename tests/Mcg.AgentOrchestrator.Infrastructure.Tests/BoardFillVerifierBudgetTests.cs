using Mcg.AgentOrchestrator.App.Orchestration;

// Pure budget calculations are parallel-safe.
public sealed class BoardFillVerifierBudgetTests
{
    [Theory]
    [InlineData(1, 3)]
    [InlineData(9, 11)]
    [InlineData(50, 12)]
    [InlineData(0, 2)]
    [InlineData(-3, 2)]
    [InlineData(10, 12)]
    [InlineData(int.MaxValue, 12)]
    [InlineData(int.MinValue, 2)]
    public void Bullet_count_gets_base_plus_per_bullet_budget_with_cap(int count, int minutes)
    {
        Assert.Equal(TimeSpan.FromMinutes(minutes), BoardFillVerifierBudget.For(count));
    }

    [Fact]
    public void Increasing_bullet_count_never_decreases_budget()
    {
        for (var count = 1; count <= 50; count++)
            Assert.True(BoardFillVerifierBudget.For(count) >= BoardFillVerifierBudget.For(count - 1),
                $"Budget decreased at bullet count {count}");
    }
}
