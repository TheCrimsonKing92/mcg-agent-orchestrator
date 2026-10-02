using Mcg.AgentOrchestrator.Core;

public sealed class DeveloperCriteriaSelfCheckPromptTests
{
    [Fact]
    public void TemplateFieldIsDeveloperOnlyAndOptional()
    {
        var line = Assert.Single(AgentOutputDirectives.WorkerResultTemplateLinesForRole(AgentRole.Developer)
            .Where(l => l.StartsWith("criteria_self_check:", StringComparison.Ordinal)));
        foreach (var fragment in new[] { "one-line JSON", "zero-based", "proven|not-owned|unmet", "<=200", "<3500", "[]", "class.method", "without this round's change" })
            Assert.Contains(fragment, line, StringComparison.Ordinal);
        foreach (var role in new[] { AgentRole.Planner, AgentRole.Tester, AgentRole.Reviewer, AgentRole.Researcher })
            Assert.DoesNotContain(AgentOutputDirectives.WorkerResultTemplateLinesForRole(role),
                l => l.StartsWith("criteria_self_check:", StringComparison.Ordinal));
        Assert.DoesNotContain(AgentOutputDirectives.WorkerResultTemplateLines,
            l => l.StartsWith("criteria_self_check:", StringComparison.Ordinal));
        Assert.DoesNotContain("criteria_self_check", AgentOutputDirectives.RequiredWorkerResultFieldNamesForRole(AgentRole.Developer));
        Assert.DoesNotContain("criteria_self_check", AgentOutputDirectives.WorkerResultFieldNames);
    }

    [Theory]
    [InlineData(TaskComplexity.Simple)]
    [InlineData(TaskComplexity.Complex)]
    public void BothRequirementFormsCarryProcedureAndAttestation(TaskComplexity complexity)
    {
        var developer = SdlcRolePromptRequirements.BuildPlainText(AgentRole.Developer, complexity);
        var procedure = Assert.Single(developer.Split('\n').Where(l => l.Contains("criteria_self_check", StringComparison.Ordinal)));
        foreach (var fragment in new[] { "Before WORKER_RESULT", "Developer as owner", "open the test", "specific named outcome", "not a weaker property", "pre-change code", "proven", "strengthen", "unmet", "assigned_scope_complete: false" })
            Assert.Contains(fragment, procedure, StringComparison.Ordinal);
        Assert.DoesNotContain("finding against that criterion", developer, StringComparison.Ordinal);
        foreach (var role in new[] { AgentRole.Tester, AgentRole.Reviewer })
        {
            var requirements = SdlcRolePromptRequirements.BuildPlainText(role, complexity);
            var attestation = Assert.Single(requirements.Split('\n').Where(l => l.Contains("Developer Criteria Self-Check", StringComparison.Ordinal)));
            foreach (var fragment in new[] { "proven", "criterion you attest", "test exists in the candidate", "assertion checks the named outcome", "finding against that criterion" })
                Assert.Contains(fragment, attestation, StringComparison.Ordinal);
        }
        foreach (var role in new[] { AgentRole.Planner, AgentRole.Researcher })
        {
            var requirements = SdlcRolePromptRequirements.BuildPlainText(role, complexity);
            Assert.DoesNotContain("criteria_self_check", requirements, StringComparison.Ordinal);
            Assert.DoesNotContain("Developer Criteria Self-Check", requirements, StringComparison.Ordinal);
        }
    }
}
