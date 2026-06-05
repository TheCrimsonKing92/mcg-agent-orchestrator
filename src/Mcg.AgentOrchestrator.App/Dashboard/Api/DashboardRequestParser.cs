using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Dashboard.Api;

internal static partial class DashboardRequestParser
{
public static string ParseAnswerSubmission(string body)
{
    if (string.IsNullOrWhiteSpace(body))
    {
        throw new ArgumentException("Answer body cannot be empty.");
    }

    var trimmed = body.Trim();
    if (!trimmed.StartsWith("{", StringComparison.Ordinal))
    {
        return trimmed;
    }

    var submission = JsonSerializer.Deserialize<AnswerSubmissionDto>(trimmed, DashboardJson.Options());
    if (string.IsNullOrWhiteSpace(submission?.Answer))
    {
        throw new ArgumentException("Answer JSON must include a non-empty 'answer' value.");
    }

    return submission.Answer;
}

public static CreateGoalSubmissionDto ParseCreateGoalSubmission(string body)
{
    if (string.IsNullOrWhiteSpace(body))
    {
        throw new ArgumentException("Goal objective cannot be empty.");
    }

    var trimmed = body.Trim();
    if (!trimmed.StartsWith("{", StringComparison.Ordinal))
    {
        return new CreateGoalSubmissionDto(trimmed);
    }

    using var document = JsonDocument.Parse(trimmed);
    var hasAutoHandoff = document.RootElement.TryGetProperty("autoHandoff", out _) ||
        document.RootElement.TryGetProperty("AutoHandoff", out _);
    var submission = document.RootElement.Deserialize<CreateGoalSubmissionDto>(DashboardJson.Options());
    if (string.IsNullOrWhiteSpace(submission?.Objective))
    {
        throw new ArgumentException("Goal JSON must include a non-empty 'objective' value.");
    }

    var workflow = string.IsNullOrWhiteSpace(submission.Workflow) ? null : submission.Workflow.Trim();
    if (workflow is not null &&
        !workflow.Equals("simple", StringComparison.OrdinalIgnoreCase) &&
        !workflow.Equals("sdlc", StringComparison.OrdinalIgnoreCase))
    {
        throw new ArgumentException("Goal workflow must be 'simple' or 'sdlc'.");
    }

    return new CreateGoalSubmissionDto(
        submission.Objective.Trim(),
        workflow?.ToLowerInvariant(),
        hasAutoHandoff ? submission.AutoHandoff : true);
}

public static AddTaskSubmissionDto ParseAddTaskSubmission(string body)
{
    if (string.IsNullOrWhiteSpace(body))
    {
        throw new ArgumentException("Task JSON body cannot be empty.");
    }

    var request = JsonSerializer.Deserialize<AddTaskRequestDto>(body, DashboardJson.Options());
    if (request is null)
    {
        throw new ArgumentException("Task JSON body was invalid.");
    }

    if (string.IsNullOrWhiteSpace(request.Role))
    {
        throw new ArgumentException("Task JSON must include a non-empty 'role' value.");
    }

    if (string.IsNullOrWhiteSpace(request.Description))
    {
        throw new ArgumentException("Task JSON must include a non-empty 'description' value.");
    }

    return new AddTaskSubmissionDto(
        CliArgumentParser.ParseAgentRole(request.Role),
        request.Description.Trim(),
        request.Delegate,
        string.IsNullOrWhiteSpace(request.VerificationPlan) ? null : request.VerificationPlan.Trim());
}

}


