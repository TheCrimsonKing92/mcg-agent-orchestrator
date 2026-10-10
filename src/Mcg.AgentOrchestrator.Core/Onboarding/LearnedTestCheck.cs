namespace Mcg.AgentOrchestrator.Core;

/// <summary>A test check supported by a confident learned runner fact.</summary>
public sealed record LearnedTestCheck(string ProjectPath, string Runner, FactConfidence Confidence, FactSource Source);
