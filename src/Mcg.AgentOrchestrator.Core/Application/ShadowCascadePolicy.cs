namespace Mcg.AgentOrchestrator.Core;

public static class ShadowCascadePolicy
{
    public const string PolicyVersion = "shadow-cascade-v1";

    // This table describes shadow choices only; live selection never consumes it.
    private static readonly (AgentRole Role, DispatchTaskClass Class, string Effort)[] Rows =
    [
        (AgentRole.Developer, DispatchTaskClass.PureMove, "low"),
        (AgentRole.Developer, DispatchTaskClass.DocsOnly, "low"),
        (AgentRole.Tester, DispatchTaskClass.PureMove, "low"),
        (AgentRole.Tester, DispatchTaskClass.DocsOnly, "low")
    ];

    public static DispatchShadowDecision Decide(AgentRole role, DispatchTaskClass taskClass,
        string? provider, string? model, string? effort)
    {
        var row = Rows.FirstOrDefault(row => row.Role == role && row.Class == taskClass);
        var shadowEffort = row.Effort ?? effort;
        return new(taskClass, provider, model, shadowEffort, PolicyVersion,
            !string.Equals(shadowEffort, effort, StringComparison.OrdinalIgnoreCase));
    }
}
