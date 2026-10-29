using Mcg.AgentOrchestrator.App.OwnerConsole;

// Parallel-safe: pure vocabulary translation with no shared state or I/O.
public sealed class OwnerActivityTextTests
{
    [Theory]
    [InlineData("waiting_for_approval", "waiting for your approval")]
    [InlineData(null, "a blocker needs review")]
    [InlineData("   ", "a blocker needs review")]
    [InlineData("question=Should we retry?", "Should we retry?")]
    [InlineData("reason=main_red", "main must be repaired before landing")]
    [InlineData("sticky_ticket", "persistent issue")]
    public void WaitingOn_CategoryQuestionOrMissingReason_ReturnsOwnerWording(
        string? value, string expected)
    {
        Assert.Equal(expected, OwnerActivityText.WaitingOn(value));
    }

    [Theory]
    [InlineData(null, "a blocker needs review")]
    [InlineData("Owner Review pending", "waiting for your approval")]
    [InlineData("HUMAN_INPUT", "waiting for your answer")]
    [InlineData("history_divergence", "the goal's history needs review")]
    [InlineData("slot_wait", "waiting for test or worker capacity")]
    [InlineData("main", "main must be repaired before landing")]
    [InlineData("gate_pending", "waiting for verification to finish")]
    [InlineData("cohort-tick", "a blocker needs review")]
    public void Blocker_CategoryOrUnknownReason_ReturnsOwnerWording(
        string? value, string expected)
    {
        Assert.Equal(expected, OwnerActivityText.Blocker(value));
    }

    [Theory]
    [InlineData(null, "fallback", "fallback")]
    [InlineData("   ", "fb", "fb")]
    [InlineData("  tick   eventKind  ", "fb", "step event type")]
    [InlineData("Receipt from CANARY", "", "proof from main check")]
    [InlineData("none", "", "no issue")]
    [InlineData("child result handoff cohort", "", "test result switch joint run")]
    [InlineData("ticket", "", "issue")]
    public void Words_InternalVocabularyOrBlankValue_ReturnsTranslationOrFallback(
        string? value, string fallback, string expected)
    {
        Assert.Equal(expected, OwnerActivityText.Words(value, fallback));
    }
}
