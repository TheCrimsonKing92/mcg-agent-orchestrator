namespace Mcg.AgentOrchestrator.App.Cli;

internal sealed record EpicProgressMemberGoal(
    string Id, EpicProgressBucket Bucket, string Status, DateTimeOffset? UpdatedAt, string Title);
