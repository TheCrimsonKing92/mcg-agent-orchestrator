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

    [Xunit.Theory(DisplayName = "Stable_slot_MTP_no_build_cache_miss_builds_before_discovery")]
    [Xunit.InlineData(false, false, true)]
    [Xunit.InlineData(false, true, true)]
    [Xunit.InlineData(true, false, true)]
    [Xunit.InlineData(true, true, false)]
    public void MtpBuildDecisionPreventsNoBuildDiscoveryFailure(
        bool noBuild,
        bool executableExists,
        bool expectedBuild)
    {
        Assert.Equal(
            expectedBuild,
            CliCommandHandlers.ShouldBuildStableSlotMtpProject(noBuild, executableExists));
    }
}
