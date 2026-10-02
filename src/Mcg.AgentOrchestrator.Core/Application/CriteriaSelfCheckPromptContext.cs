namespace Mcg.AgentOrchestrator.Core;

// Owns only the optional self-check additions; existing instructions stay in the brief.
public static class CriteriaSelfCheckPromptContext
{
    public const string FileName = "criteria-self-check.md";

    public static string? BuildArtifact(AgentRole role) => role switch
    {
        AgentRole.Developer => string.Join(Environment.NewLine,
            SdlcRolePromptRequirements.DeveloperSelfCheckProcedure,
            AgentOutputDirectives.WorkerResultTemplateLinesForRole(role).Single(IsTemplateField)),
        AgentRole.Tester or AgentRole.Reviewer => SdlcRolePromptRequirements.SelfCheckAttestation,
        _ => null
    };

    internal static void Externalize(List<string> lines, AgentRole role, bool includeReference)
    {
        if (BuildArtifact(role) is null)
            return;

        lines.RemoveAll(line => IsTemplateField(line) ||
            line == SdlcRolePromptRequirements.DeveloperSelfCheckProcedure ||
            line == SdlcRolePromptRequirements.SelfCheckAttestation);
        if (includeReference)
            lines.Add($"Read {FileName} in the context directory before WORKER_RESULT.");
    }

    private static bool IsTemplateField(string line) =>
        line.StartsWith("criteria_self_check:", StringComparison.Ordinal);
}
