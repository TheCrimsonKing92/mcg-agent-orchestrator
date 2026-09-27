namespace Mcg.AgentOrchestrator.Core;

public sealed partial class TaskSpec
{
    // The retry round current when feedback was recorded, before the next retry is applied.
    public DateTimeOffset? CriterionRetryFeedbackRoundAt { get; private set; }

    internal void RecordCriterionRetryFeedback(IReadOnlyList<string> feedback, DateTimeOffset? roundAt)
    {
        RecordCriterionRetryFeedback(feedback);
        CriterionRetryFeedbackRoundAt = roundAt;
    }
}
