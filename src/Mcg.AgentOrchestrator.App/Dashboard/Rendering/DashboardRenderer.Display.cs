using Mcg.AgentOrchestrator.App.Rendering;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Dashboard.Rendering;

public static partial class DashboardRenderer
{
    private static string Display(GoalStatus status) => DashboardDisplayNames.Display(status);
    private static string Display(WorkTaskStatus status) => DashboardDisplayNames.Display(status);
    private static string Display(ProgressKind kind) => DashboardDisplayNames.Display(kind);
    private static string Display(TaskAttentionKind kind) => DashboardDisplayNames.Display(kind);
    private static string Display(NextActionKind kind) => DashboardDisplayNames.Display(kind);
    private static string Display(VerificationGateStatus status) => DashboardDisplayNames.Display(status);
    private static string Display(GoalAcceptanceBlockerKind kind) => DashboardDisplayNames.Display(kind);
    private static string Display(TaskEvidenceKind kind) => DashboardDisplayNames.Display(kind);
    private static string Display(StageReadinessStatus status) => DashboardDisplayNames.Display(status);
    private static string Display(AgentExecutionPolicy policy) => DashboardDisplayNames.Display(policy);
}
