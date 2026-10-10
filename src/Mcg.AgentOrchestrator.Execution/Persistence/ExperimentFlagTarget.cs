using System.Text.Json.Serialization;

namespace Mcg.AgentOrchestrator.Infrastructure;

public enum ExperimentFlagFileKind { ConductorPolicy = 1, RemoteLaneExecutors = 2 }

/// <summary>A boolean intervention; PriorValue is captured once before the first host-state write.</summary>
public sealed record ExperimentFlagTarget(ExperimentFlagFileKind FileKind, string PropertyName,
    [property: JsonRequired] bool ValueToApply, bool? PriorValue = null);
