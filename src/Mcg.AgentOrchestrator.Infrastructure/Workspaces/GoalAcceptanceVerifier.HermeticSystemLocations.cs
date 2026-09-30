namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed partial class GoalAcceptanceVerifier
{
    private static readonly string[] HermeticSystemLocationVariables =
    [
        "SystemDrive",
        "ProgramData",
        "ALLUSERSPROFILE",
        "PUBLIC",
        "CommonProgramFiles",
        "CommonProgramFiles(x86)",
        "CommonProgramW6432"
    ];

    private static bool IsHermeticSystemLocationVariable(string name) =>
        HermeticSystemLocationVariables.Contains(name, StringComparer.OrdinalIgnoreCase);
}
