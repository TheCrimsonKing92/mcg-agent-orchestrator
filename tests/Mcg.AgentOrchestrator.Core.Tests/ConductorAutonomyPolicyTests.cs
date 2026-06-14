using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;

namespace Mcg.AgentOrchestrator.Core.Tests;

public sealed class ConductorAutonomyPolicyTests
{
    [Xunit.Fact(DisplayName = "ConductorAutonomyPolicy_Conservative_is_default_preset")]
    public void ConductorAutonomyPolicyConservativeIsDefaultPreset()
    {
        Assert.Equal("Conservative", ConductorAutonomyPolicy.Default.Name);
        Assert.True(ReferenceEquals(ConductorAutonomyPolicy.Default, ConductorAutonomyPolicy.Conservative));
    }

    [Xunit.Fact(DisplayName = "ConductorAutonomyPolicy_three_named_presets_exist")]
    public void ConductorAutonomyPolicyThreeNamedPresetsExist()
    {
        var all = ConductorAutonomyPolicy.All;
        Assert.Equal(3, all.Count);
        Assert.Contains(all, p => p.Name == "Conservative");
        Assert.Contains(all, p => p.Name == "Permissive");
        Assert.Contains(all, p => p.Name == "Manual");
    }

    [Xunit.Fact(DisplayName = "ConductorAutonomyPolicy_presets_have_positive_budget_and_workers")]
    public void ConductorAutonomyPolicyPresetsHavePositiveBudgetAndWorkers()
    {
        foreach (var policy in ConductorAutonomyPolicy.All)
        {
            Assert.True(policy.MaxConcurrentPaidWorkers > 0,
                $"{policy.Name}: MaxConcurrentPaidWorkers must be > 0");
            Assert.True(policy.MaxTotalBudget > 0,
                $"{policy.Name}: MaxTotalBudget must be > 0");
        }
    }

    [Xunit.Fact(DisplayName = "ConductorAutonomyPolicy_presets_contain_all_lifecycle_states")]
    public void ConductorAutonomyPolicyPresetsContainAllLifecycleStates()
    {
        var allStates = Enum.GetValues<GoalLifecycleState>();
        foreach (var policy in ConductorAutonomyPolicy.All)
        {
            foreach (var state in allStates)
            {
                Assert.True(policy.TransitionMap.ContainsKey(state),
                    $"{policy.Name} is missing lifecycle state {state}");
            }
        }
    }

    [Xunit.Fact(DisplayName = "ConductorAutonomyPolicy_Conservative_escalates_at_Failed_Blocked_AwaitingHumanInput")]
    public void ConductorAutonomyPolicyConservativeEscalatesAtErrorStates()
    {
        var policy = ConductorAutonomyPolicy.Conservative;
        Assert.Equal(ConductorTransitionDecision.Escalate, policy.GetTransitionDecision(GoalLifecycleState.Failed));
        Assert.Equal(ConductorTransitionDecision.Escalate, policy.GetTransitionDecision(GoalLifecycleState.Blocked));
        Assert.Equal(ConductorTransitionDecision.Escalate, policy.GetTransitionDecision(GoalLifecycleState.AwaitingHumanInput));
    }

    [Xunit.Fact(DisplayName = "ConductorAutonomyPolicy_Conservative_autos_mechanical_transitions")]
    public void ConductorAutonomyPolicyConservativeAutosMechanicalTransitions()
    {
        var policy = ConductorAutonomyPolicy.Conservative;
        Assert.Equal(ConductorTransitionDecision.Auto, policy.GetTransitionDecision(GoalLifecycleState.Created));
        Assert.Equal(ConductorTransitionDecision.Auto, policy.GetTransitionDecision(GoalLifecycleState.WorkspaceReady));
        Assert.Equal(ConductorTransitionDecision.Auto, policy.GetTransitionDecision(GoalLifecycleState.Dispatched));
        Assert.Equal(ConductorTransitionDecision.Auto, policy.GetTransitionDecision(GoalLifecycleState.Running));
        Assert.Equal(ConductorTransitionDecision.Auto, policy.GetTransitionDecision(GoalLifecycleState.AwaitingVerification));
        Assert.Equal(ConductorTransitionDecision.Auto, policy.GetTransitionDecision(GoalLifecycleState.Verified));
        Assert.Equal(ConductorTransitionDecision.Auto, policy.GetTransitionDecision(GoalLifecycleState.Recorded));
        Assert.Equal(ConductorTransitionDecision.Auto, policy.GetTransitionDecision(GoalLifecycleState.CleanedUp));
    }

    [Xunit.Fact(DisplayName = "ConductorAutonomyPolicy_Conservative_escalates_Merged_by_default")]
    public void ConductorAutonomyPolicyConservativeEscalatesMergedByDefault()
    {
        var policy = ConductorAutonomyPolicy.Conservative;
        // No changeRisk provided — falls back to the base map decision (Escalate)
        Assert.Equal(ConductorTransitionDecision.Escalate, policy.GetTransitionDecision(GoalLifecycleState.Merged));
    }

    [Xunit.Fact(DisplayName = "ConductorAutonomyPolicy_Conservative_auto_promotes_Merged_for_DocsOnly_change")]
    public void ConductorAutonomyPolicyConservativeAutoPromotesMergedForDocsOnly()
    {
        var policy = ConductorAutonomyPolicy.Conservative;
        Assert.Equal(
            ConductorTransitionDecision.Auto,
            policy.GetTransitionDecision(GoalLifecycleState.Merged, ChangeRiskTier.DocsOnly));
    }

    [Xunit.Fact(DisplayName = "ConductorAutonomyPolicy_Conservative_escalates_Merged_for_Behavior_and_above")]
    public void ConductorAutonomyPolicyConservativeEscalatesMergedForHigherRisk()
    {
        var policy = ConductorAutonomyPolicy.Conservative;
        Assert.Equal(ConductorTransitionDecision.Escalate,
            policy.GetTransitionDecision(GoalLifecycleState.Merged, ChangeRiskTier.Behavior));
        Assert.Equal(ConductorTransitionDecision.Escalate,
            policy.GetTransitionDecision(GoalLifecycleState.Merged, ChangeRiskTier.Build));
        Assert.Equal(ConductorTransitionDecision.Escalate,
            policy.GetTransitionDecision(GoalLifecycleState.Merged, ChangeRiskTier.Security));
        Assert.Equal(ConductorTransitionDecision.Escalate,
            policy.GetTransitionDecision(GoalLifecycleState.Merged, ChangeRiskTier.Broad));
    }

    [Xunit.Fact(DisplayName = "ConductorAutonomyPolicy_Permissive_autos_all_states")]
    public void ConductorAutonomyPolicyPermissiveAutosAllStates()
    {
        var policy = ConductorAutonomyPolicy.Permissive;
        foreach (var state in Enum.GetValues<GoalLifecycleState>())
        {
            Assert.True(policy.GetTransitionDecision(state) == ConductorTransitionDecision.Auto,
                $"Permissive should Auto at {state}");
        }
    }

    [Xunit.Fact(DisplayName = "ConductorAutonomyPolicy_Permissive_autos_Merged_for_any_risk_tier")]
    public void ConductorAutonomyPolicyPermissiveAutosMergedForAnyRisk()
    {
        var policy = ConductorAutonomyPolicy.Permissive;
        foreach (var tier in Enum.GetValues<ChangeRiskTier>())
        {
            Assert.True(
                policy.GetTransitionDecision(GoalLifecycleState.Merged, tier) == ConductorTransitionDecision.Auto,
                $"Permissive should Auto at Merged for {tier}");
        }
    }

    [Xunit.Fact(DisplayName = "ConductorAutonomyPolicy_Manual_escalates_all_states")]
    public void ConductorAutonomyPolicyManualEscalatesAllStates()
    {
        var policy = ConductorAutonomyPolicy.Manual;
        foreach (var state in Enum.GetValues<GoalLifecycleState>())
        {
            Assert.True(policy.GetTransitionDecision(state) == ConductorTransitionDecision.Escalate,
                $"Manual should Escalate at {state}");
        }
    }

    [Xunit.Fact(DisplayName = "ConductorAutonomyPolicy_Manual_escalates_Merged_even_for_DocsOnly")]
    public void ConductorAutonomyPolicyManualEscalatesMergedEvenForDocsOnly()
    {
        var policy = ConductorAutonomyPolicy.Manual;
        // Manual has null AutoPromoteRiskThreshold; no risk-based upgrade should occur
        Assert.Equal(ConductorTransitionDecision.Escalate,
            policy.GetTransitionDecision(GoalLifecycleState.Merged, ChangeRiskTier.DocsOnly));
    }

    [Xunit.Fact(DisplayName = "ConductorAutonomyPolicy_validation_rejects_zero_workers")]
    public void ConductorAutonomyPolicyValidationRejectsZeroWorkers()
    {
        var policy = ConductorAutonomyPolicy.Conservative with { MaxConcurrentPaidWorkers = 0 };
        var errors = policy.Validate();
        Assert.True(errors.Count > 0);
        Assert.Contains(errors, e => e.Contains("maxConcurrentPaidWorkers", StringComparison.Ordinal));
    }

    [Xunit.Fact(DisplayName = "ConductorAutonomyPolicy_validation_rejects_negative_budget")]
    public void ConductorAutonomyPolicyValidationRejectsNegativeBudget()
    {
        var policy = ConductorAutonomyPolicy.Conservative with { MaxTotalBudget = -1m };
        var errors = policy.Validate();
        Assert.True(errors.Count > 0);
        Assert.Contains(errors, e => e.Contains("maxTotalBudget", StringComparison.Ordinal));
    }

    [Xunit.Fact(DisplayName = "ConductorAutonomyPolicy_validation_rejects_provider_cap_exceeding_total")]
    public void ConductorAutonomyPolicyValidationRejectsProviderCapExceedingTotal()
    {
        var policy = ConductorAutonomyPolicy.Conservative with
        {
            MaxTotalBudget = 5m,
            PerProviderBudgetCaps = new Dictionary<string, decimal> { ["anthropic"] = 10m }
        };
        var errors = policy.Validate();
        Assert.True(errors.Count > 0);
        Assert.Contains(errors, e => e.Contains("anthropic", StringComparison.Ordinal) && e.Contains("exceeds", StringComparison.Ordinal));
    }

    [Xunit.Fact(DisplayName = "ConductorAutonomyPolicy_validation_rejects_missing_lifecycle_state")]
    public void ConductorAutonomyPolicyValidationRejectsMissingLifecycleState()
    {
        var incomplete = Enum.GetValues<GoalLifecycleState>()
            .Where(s => s != GoalLifecycleState.Merged)
            .ToDictionary(s => s, _ => ConductorTransitionDecision.Auto);
        var policy = ConductorAutonomyPolicy.Conservative with
        {
            TransitionMap = incomplete
        };
        var errors = policy.Validate();
        Assert.True(errors.Count > 0);
        Assert.Contains(errors, e => e.Contains("Merged", StringComparison.Ordinal));
    }

    [Xunit.Fact(DisplayName = "ConductorAutonomyPolicy_JSON_round_trip_Conservative")]
    public void ConductorAutonomyPolicyJsonRoundTripConservative()
    {
        var original = ConductorAutonomyPolicy.Conservative;
        var json = original.ToJson();
        var restored = ConductorAutonomyPolicy.ParseJson(json);

        Assert.Equal(original.Name, restored.Name);
        Assert.Equal(original.MaxConcurrentPaidWorkers, restored.MaxConcurrentPaidWorkers);
        Assert.Equal(original.MaxTotalBudget, restored.MaxTotalBudget);
        Assert.Equal(original.AutoPromoteRiskThreshold, restored.AutoPromoteRiskThreshold);
        foreach (var state in Enum.GetValues<GoalLifecycleState>())
        {
            Assert.True(original.TransitionMap[state] == restored.TransitionMap[state],
                $"TransitionMap mismatch at {state}");
        }
    }

    [Xunit.Fact(DisplayName = "ConductorAutonomyPolicy_JSON_round_trip_Manual_with_null_threshold")]
    public void ConductorAutonomyPolicyJsonRoundTripManualNullThreshold()
    {
        var original = ConductorAutonomyPolicy.Manual;
        var json = original.ToJson();
        var restored = ConductorAutonomyPolicy.ParseJson(json);

        Assert.Equal(original.Name, restored.Name);
        Assert.True(!restored.AutoPromoteRiskThreshold.HasValue,
            "Manual AutoPromoteRiskThreshold should round-trip as null");
    }

    [Xunit.Fact(DisplayName = "ConductorAutonomyPolicy_JSON_round_trip_with_provider_caps")]
    public void ConductorAutonomyPolicyJsonRoundTripWithProviderCaps()
    {
        var original = ConductorAutonomyPolicy.Permissive with
        {
            PerProviderBudgetCaps = new Dictionary<string, decimal>
            {
                ["anthropic"] = 15m,
                ["openai"] = 10m
            }
        };
        var json = original.ToJson();
        var restored = ConductorAutonomyPolicy.ParseJson(json);

        Assert.True(restored.PerProviderBudgetCaps is not null);
        Assert.Equal(15m, restored.PerProviderBudgetCaps!["anthropic"]);
        Assert.Equal(10m, restored.PerProviderBudgetCaps!["openai"]);
    }

    [Xunit.Fact(DisplayName = "ConductorAutonomyPolicy_ParseJson_rejects_invalid_JSON")]
    public void ConductorAutonomyPolicyParseJsonRejectsInvalidJson()
    {
        var ex = Assert.Throws<FormatException>(() =>
            ConductorAutonomyPolicy.ParseJson("{ not valid json ~~~"));
        Assert.True(ex.Message.Contains("not valid JSON", StringComparison.Ordinal));
    }

    [Xunit.Fact(DisplayName = "ConductorAutonomyPolicy_ParseJson_rejects_unknown_lifecycle_state")]
    public void ConductorAutonomyPolicyParseJsonRejectsUnknownLifecycleState()
    {
        var json = """
            {
              "name": "test",
              "maxConcurrentPaidWorkers": 1,
              "maxTotalBudget": 5.0,
              "autoPromoteRiskThreshold": "DocsOnly",
              "transitionMap": {
                "Created": "Auto",
                "NotARealState": "Auto"
              }
            }
            """;
        var ex = Assert.Throws<FormatException>(() => ConductorAutonomyPolicy.ParseJson(json));
        Assert.True(ex.Message.Contains("NotARealState", StringComparison.Ordinal));
    }

    [Xunit.Fact(DisplayName = "ConductorAutonomyPolicy_ParseJson_rejects_unknown_risk_tier")]
    public void ConductorAutonomyPolicyParseJsonRejectsUnknownRiskTier()
    {
        var json = """
            {
              "name": "test",
              "maxConcurrentPaidWorkers": 1,
              "maxTotalBudget": 5.0,
              "autoPromoteRiskThreshold": "NotARealTier",
              "transitionMap": {}
            }
            """;
        var ex = Assert.Throws<FormatException>(() => ConductorAutonomyPolicy.ParseJson(json));
        Assert.True(ex.Message.Contains("NotARealTier", StringComparison.Ordinal));
    }

    [Xunit.Fact(DisplayName = "ConductorAutonomyPolicy_ParseJson_rejects_bad_transition_decision")]
    public void ConductorAutonomyPolicyParseJsonRejectsBadTransitionDecision()
    {
        var json = """
            {
              "name": "test",
              "maxConcurrentPaidWorkers": 1,
              "maxTotalBudget": 5.0,
              "autoPromoteRiskThreshold": "DocsOnly",
              "transitionMap": {
                "Created": "Maybe"
              }
            }
            """;
        var ex = Assert.Throws<FormatException>(() => ConductorAutonomyPolicy.ParseJson(json));
        Assert.True(ex.Message.Contains("Maybe", StringComparison.Ordinal));
        Assert.True(ex.Message.Contains("Auto", StringComparison.Ordinal));
    }

    [Xunit.Fact(DisplayName = "ConductorAutonomyPolicy_ParseJson_reports_source_path_in_errors")]
    public void ConductorAutonomyPolicyParseJsonReportsSourcePathInErrors()
    {
        var ex = Assert.Throws<FormatException>(() =>
            ConductorAutonomyPolicy.ParseJson("not json", "/repo/.orchestrator/conductor-policy.json"));
        Assert.True(ex.Message.Contains("/repo/.orchestrator/conductor-policy.json", StringComparison.Ordinal));
    }

    [Xunit.Fact(DisplayName = "ConductorAutonomyPolicy_LoadFromOrchestratorDirectory_returns_Conservative_when_no_file")]
    public void ConductorAutonomyPolicyLoadFromOrchestratorDirectoryReturnsConservativeWhenNoFile()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        try
        {
            Directory.CreateDirectory(tempDir);
            var policy = ConductorAutonomyPolicy.LoadFromOrchestratorDirectory(tempDir);
            Assert.Equal("Conservative", policy.Name);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Xunit.Fact(DisplayName = "ConductorAutonomyPolicy_LoadFromOrchestratorDirectory_loads_valid_file")]
    public void ConductorAutonomyPolicyLoadFromOrchestratorDirectoryLoadsValidFile()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        try
        {
            Directory.CreateDirectory(Path.Combine(tempDir, ".orchestrator"));
            var json = ConductorAutonomyPolicy.Permissive.ToJson();
            File.WriteAllText(Path.Combine(tempDir, ".orchestrator", "conductor-policy.json"), json);

            var policy = ConductorAutonomyPolicy.LoadFromOrchestratorDirectory(tempDir);
            Assert.Equal("Permissive", policy.Name);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Xunit.Fact(DisplayName = "ConductorAutonomyPolicy_ChangeRiskTier_ordered_DocsOnly_to_Broad")]
    public void ConductorAutonomyPolicyChangeRiskTierOrdered()
    {
        Assert.True((int)ChangeRiskTier.DocsOnly < (int)ChangeRiskTier.Behavior);
        Assert.True((int)ChangeRiskTier.Behavior < (int)ChangeRiskTier.Build);
        Assert.True((int)ChangeRiskTier.Build < (int)ChangeRiskTier.Security);
        Assert.True((int)ChangeRiskTier.Security < (int)ChangeRiskTier.Broad);
    }
}
