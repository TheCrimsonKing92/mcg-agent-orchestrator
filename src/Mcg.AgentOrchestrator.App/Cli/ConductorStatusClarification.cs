namespace Mcg.AgentOrchestrator.App.Cli;

internal sealed record ConductorStatusClarification(
    string Id, DateTimeOffset RaisedAt, string Hold, string AnswerCommand);
