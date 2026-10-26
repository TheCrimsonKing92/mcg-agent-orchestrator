using Mcg.AgentOrchestrator.App.OwnerConsole;

// Parallel-safe: all age changes use injected times, with no clock or IO dependency.
public sealed class OwnerConsoleNotificationDetectorDecisionTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void EachDecisionCrossesOnceAndLeavingForgetsTheId()
    {
        var detector = new OwnerConsoleNotificationDetector(new());
        var first = Decision("first", Now.AddMinutes(-29));
        var model = Model(first);
        detector.Detect(null, model, Now, OwnerConsoleBellTiers.All);
        Assert.Equal([OwnerConsoleNotificationTier.Attention], detector.Detect(model, model, Now.AddMinutes(2), OwnerConsoleBellTiers.All));
        for (var minute = 3; minute <= 5; minute++)
            Assert.Empty(detector.Detect(model, model, Now.AddMinutes(minute), OwnerConsoleBellTiers.All));
        var second = Model(first, Decision("second", Now.AddMinutes(-23)));
        Assert.Empty(detector.Detect(model, second, Now.AddMinutes(6), OwnerConsoleBellTiers.All));
        Assert.Equal([OwnerConsoleNotificationTier.Attention], detector.Detect(second, second, Now.AddMinutes(8), OwnerConsoleBellTiers.All));
        var empty = Model();
        Assert.Empty(detector.Detect(second, empty, Now.AddMinutes(9), OwnerConsoleBellTiers.All));
        var fresh = Model(Decision("new", Now.AddMinutes(9)), Decision("first", Now.AddMinutes(9)));
        Assert.Empty(detector.Detect(empty, fresh, Now.AddMinutes(10), OwnerConsoleBellTiers.All));
        Assert.Equal([OwnerConsoleNotificationTier.Attention], detector.Detect(fresh, fresh, Now.AddMinutes(39), OwnerConsoleBellTiers.All));
    }

    [Fact]
    public void CustomThresholdChangesTheCrossing()
    {
        var detector = new OwnerConsoleNotificationDetector(new() { AttentionThreshold = TimeSpan.FromMinutes(5) });
        var model = Model(Decision("first", Now.AddMinutes(-4)));
        detector.Detect(null, model, Now, OwnerConsoleBellTiers.All);
        Assert.Equal([OwnerConsoleNotificationTier.Attention], detector.Detect(model, model, Now.AddMinutes(1), OwnerConsoleBellTiers.All));
    }

    [Fact]
    public void FreshInitiallyOldAndUntimedDecisionsStaySilent()
    {
        var detector = new OwnerConsoleNotificationDetector(new());
        var empty = Model();
        detector.Detect(null, empty, Now, OwnerConsoleBellTiers.All);
        var model = Model(Decision("fresh", Now.AddMinutes(-1)), Decision("old", Now.AddMinutes(-45)), Decision("untimed", null));
        Assert.Empty(detector.Detect(empty, model, Now, OwnerConsoleBellTiers.All));
        Assert.Empty(detector.Detect(model, model, Now.AddMinutes(1), OwnerConsoleBellTiers.All));
        var withoutFresh = Model(model.Decisions[1], model.Decisions[2]);
        Assert.Empty(detector.Detect(model, withoutFresh, Now.AddHours(1), OwnerConsoleBellTiers.All));
    }

    private static OwnerConsoleDecision Decision(string id, DateTimeOffset? raised) =>
        new(id, 1, "goal", "goal", OwnerQuestionKind.HumanInput, "Answer?", "Answer?", null, null, null, raised);
    private static OwnerConsoleViewModel Model(params OwnerConsoleDecision[] decisions) =>
        new(new(false, 0, decisions.Length, 0, null, 0), [.. decisions], [], []);
}
