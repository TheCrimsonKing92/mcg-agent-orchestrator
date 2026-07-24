using Mcg.AgentOrchestrator.App.Cli;

public sealed class StableSlotMtpFilterTests
{
    [Xunit.Fact(DisplayName = "Stable_slot_MTP_filter_accepts_bare_focused_test_class_names")]
    public void BareFocusedTestClassNamesAreTranslated()
    {
        var arguments = CliCommandHandlers.TranslateStableSlotMtpFilter(
            "AutoReviewRetryConvergenceBriefBuilderTests|WorkerDispatchTestsDispatchPreparation");

        Assert.Equal(
            [
                "--filter-class",
                "*AutoReviewRetryConvergenceBriefBuilderTests*",
                "--filter-class",
                "*WorkerDispatchTestsDispatchPreparation*"
            ],
            arguments);
    }
}
