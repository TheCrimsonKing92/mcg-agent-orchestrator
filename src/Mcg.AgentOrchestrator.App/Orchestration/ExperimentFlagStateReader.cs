using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

/// <summary>Reads the targeted host flag for both keep and revert eligibility.</summary>
internal sealed class ExperimentFlagStateReader(string policyPath, string executorsPath)
{
    internal bool? Read(ExperimentFlagTarget target) => target.FileKind switch
    {
        ExperimentFlagFileKind.ConductorPolicy => File.Exists(policyPath)
            ? ConductorPolicyBooleanFlags.Read(ConductorAutonomyPolicy.ParseJson(File.ReadAllText(policyPath)), target.PropertyName)
            : null,
        ExperimentFlagFileKind.RemoteLaneExecutors => RemoteLaneExecutorFlags.Read(executorsPath, target.PropertyName),
        _ => null
    };
}
