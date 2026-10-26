using System.Reflection;

// Parallel-safe: inspects assembly metadata; the negative control filters an in-memory type list.
public sealed class OwnerConsoleMovedClassesAssemblyTests
{
    private static readonly string[] MovedClassNames =
    [
        "OwnerConsoleAcceptAndBellTests",
        "OwnerConsoleActivityDaySeparatorScenarioTests",
        "OwnerConsoleActivityFilterScenarioTests",
        "OwnerConsoleActivityFilterTests",
        "OwnerConsoleActivityFollowScenarioTests",
        "OwnerConsoleActivityFollowTests",
        "OwnerConsoleActivityHistoryTests",
        "OwnerConsoleActivityLoaderScopeTests",
        "OwnerConsoleActivityModeTitleScenarioTests",
        "OwnerConsoleActivityOutcomeTests",
        "OwnerConsoleActivityPresentationTests",
        "OwnerConsoleActivityRowsTests",
        "OwnerConsoleActivitySendBackQuestionTests",
        "OwnerConsoleActivityTitleTests",
        "OwnerConsoleAnswerTrackingTests",
        "OwnerConsoleBellTierCommandTests",
        "OwnerConsoleBindingsTests",
        "OwnerConsoleBoardAttentionTests",
        "OwnerConsoleBoardFirstStartupTests",
        "OwnerConsoleBoardTests",
        "OwnerConsoleChangeStreamSessionTests",
        "OwnerConsoleCommandCompletionScenarioTests",
        "OwnerConsoleCommandCompletionTests",
        "OwnerConsoleConductorCommandTests",
        "OwnerConsoleControllerConstructionGuardTests",
        "OwnerConsoleControllerQueryOnlyTests",
        "OwnerConsoleDecisionAnsweredStreamTests",
        "OwnerConsoleDecisionDetailTests",
        "OwnerConsoleDecisionRaisedAtTests",
        "OwnerConsoleDialogBindingsTests",
        "OwnerConsoleDialogKeyScenarioTests",
        "OwnerConsoleDialogOverflowScenarioTests",
        "OwnerConsoleDialogWidthTests",
        "OwnerConsoleDigestCommandTests",
        "OwnerConsoleEpicFormatterTests",
        "OwnerConsoleEpicPlanFormatterTests",
        "OwnerConsoleEpicViewTests",
        "OwnerConsoleEventPumpTests",
        "OwnerConsoleExperimentDecideTests",
        "OwnerConsoleFailureReasonTests",
        "OwnerConsoleFleetCommandTests",
        "OwnerConsoleFrameCompatibilityTests",
        "OwnerConsoleFrameOutputInstallerTests",
        "OwnerConsoleFrameReproductionTests",
        "OwnerConsoleFullScreenInputTests",
        "OwnerConsoleFullScreenPaneTests",
        "OwnerConsoleFullScreenTests",
        "OwnerConsoleGateCheckMedianScenarioTests",
        "OwnerConsoleGateCheckMedianTests",
        "OwnerConsoleGateDialogExpandTests",
        "OwnerConsoleGateFailureNamesTests",
        "OwnerConsoleGateLaneResolverTests",
        "OwnerConsoleGateLaneTruthTests",
        "OwnerConsoleGateLayoutTests",
        "OwnerConsoleGateMotionBoardTests",
        "OwnerConsoleGateNoSlotLaneTests",
        "OwnerConsoleGateOwnerScreenshotScenarioTests",
        "OwnerConsoleGateProgressTests",
        "OwnerConsoleGateRemainingEstimateTests",
        "OwnerConsoleGateViewTests",
        "OwnerConsoleGateWhereLineTests",
        "OwnerConsoleGateWhereScenarioTests",
        "OwnerConsoleGeneratedTitleScenarioTests",
        "OwnerConsoleGoalDetailControllerBoundaryTests",
        "OwnerConsoleGoalDetailFormatterTests",
        "OwnerConsoleGoalDetailImmediateOpenTests",
        "OwnerConsoleGoalDetailResolutionTests",
        "OwnerConsoleGoalDialogTests",
        "OwnerConsoleGoalGateJumpTests",
        "OwnerConsoleGoalLandingTests",
        "OwnerConsoleGoalReturnExplanationTests",
        "OwnerConsoleGoalStageActivityTests",
        "OwnerConsoleGoalTitleTests",
        "OwnerConsoleHostEvidenceRunsTests",
        "OwnerConsoleIncrementalRefreshTests",
        "OwnerConsoleInputIntegrityTests",
        "OwnerConsoleJointGateFailureTests",
        "OwnerConsoleJsonlTailTests",
        "OwnerConsoleKeyCommandScenarioTests",
        "OwnerConsoleKeysDocTests",
        "OwnerConsoleLandedTodayTests",
        "OwnerConsoleLineFitTests",
        "OwnerConsoleLiveQuestionsTests",
        "OwnerConsoleLoopResponsivenessTests",
        "OwnerConsoleMetadataOncePerRefreshTests",
        "OwnerConsoleModeSelectionTests",
        "OwnerConsoleNoticeStripTests",
        "OwnerConsoleNotificationDetectorConductorTests",
        "OwnerConsoleNotificationDetectorDecisionTests",
        "OwnerConsoleNotificationDetectorLandingTests",
        "OwnerConsoleNotificationDetectorNegativeControlTests",
        "OwnerConsoleNotificationDetectorStopReasonTests",
        "OwnerConsoleNotificationSettingsTests",
        "OwnerConsoleOverflowTests",
        "OwnerConsoleOwnerAttentionTests",
        "OwnerConsolePaneJumpTests",
        "OwnerConsolePaneOverflowScenarioTests",
        "OwnerConsolePriorityBadgeTests",
        "OwnerConsoleProgramHealthNoticeTests",
        "OwnerConsoleQuestionPushTests",
        "OwnerConsoleQuietRefreshTests",
        "OwnerConsoleReadOnlyBoundaryTests",
        "OwnerConsoleRefreshScopeTests",
        "OwnerConsoleRefreshTimingsTests",
        "OwnerConsoleScreenOperationTests",
        "OwnerConsoleStartupActivityTests",
        "OwnerConsoleStatusFreshnessTests",
        "OwnerConsoleStreamRefreshTests",
        "OwnerConsoleTextDialogOverflowTests",
        "OwnerConsoleTextPageTests",
        "OwnerConsoleTimeLabelTests",
        "OwnerConsoleVerifyingWithoutGateTests",
        "OwnerConsoleViewModelBuilderTests",
        "OwnerExperimentReadingQuestionTests",
        "OwnerQuestionAuthorDefaultTests",
        "OwnerQuestionResolutionDialogTests",
        "OwnerQuestionResolutionFieldLayoutTests",
    ];

    public static IEnumerable<object[]> MovedClasses() =>
        MovedClassNames.Select(name => new object[] { name });

    [Theory]
    [MemberData(nameof(MovedClasses))]
    public void MovedType_NestedAssembly_DeclaresTests(string typeName)
    {
        var assembly = typeof(OwnerConsoleMovedClassesAssemblyTests).Assembly;

        Assert.Empty(Violations(assembly.GetName().Name, assembly.GetTypes(), typeName));
    }

    [Fact]
    public void MovedType_MissingFromAssembly_ReportsViolation()
    {
        var assembly = typeof(OwnerConsoleMovedClassesAssemblyTests).Assembly;
        var types = assembly.GetTypes().Where(type => type.Name != "OwnerConsoleBoardTests").ToArray();

        Assert.Contains(Violations(assembly.GetName().Name, types, "OwnerConsoleBoardTests"),
            violation => violation.Contains("OwnerConsoleBoardTests", StringComparison.Ordinal));
    }

    private static IReadOnlyList<string> Violations(string? assemblyName, Type[] types, string typeName)
    {
        var violations = new List<string>();
        if (assemblyName != "Mcg.AgentOrchestrator.Infrastructure.OwnerConsole.Tests")
            violations.Add($"Unexpected assembly: {assemblyName}.");

        var matches = types.Where(type => type.Name == typeName).ToArray();
        if (matches.Length != 1)
            violations.Add($"Expected one {typeName}, found {matches.Length}.");
        else if (!matches[0].GetMethods(BindingFlags.Public | BindingFlags.Instance |
                     BindingFlags.Static | BindingFlags.DeclaredOnly)
                 .Any(method => method.GetCustomAttributes(inherit: true).OfType<FactAttribute>().Any()))
            violations.Add($"{typeName} declares no public test methods.");

        return violations;
    }
}
