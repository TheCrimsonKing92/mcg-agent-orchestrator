using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Dashboard.Api;

internal sealed record CreateGoalSubmissionDto(
    string Objective,
    string? Workflow = null,
    bool AutoHandoff = false,
    bool ConfirmAutoHandoff = false);

internal sealed record AddTaskRequestDto(string? Role, string? Description, bool? Delegate = true, string? VerificationPlan = null);

internal sealed record AddTaskSubmissionDto(AgentRole Role, string Description, bool? Delegate = true, string? VerificationPlan = null);

internal sealed record AgentSubmissionDto(
    string Role,
    string ProviderName,
    string ModelName,
    string? Name,
    string? ReasoningEffort = null,
    int? MaxOutputTokens = null,
    string? ExecutionPolicy = null,
    string? SubscriptionProfileName = null,
    string? SubscriptionModelAlias = null,
    string? SubscriptionReasoningEffort = null,
    string? ComplexProviderName = null,
    string? ComplexModelName = null,
    int? ComplexMaxOutputTokens = null,
    string? ComplexReasoningEffort = null);

internal sealed record WorkerProfileSubmissionDto(string Name, string CommandTemplate);

internal sealed record ProviderSmokeSubmissionDto(string? Target, bool ConfirmAll = false);

internal sealed record DispatchSubmissionDto(string WorkerName, string Command);

internal sealed record VerifySubmissionDto(string Command);

internal sealed record ManualVerifySubmissionDto(bool Passed, string Note);

internal sealed record ProgressSubmissionDto(string Status, string Message);

internal sealed record RetrySubmissionDto(string Message);

internal sealed record VerificationPlanSubmissionDto(string Plan);

internal sealed record AskSubmissionDto(string Question);

internal sealed record ProfileDispatchReadySubmissionDto(string ProfileName);

internal sealed record AnswerSubmissionDto(string Answer);


