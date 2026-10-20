namespace Mcg.AgentOrchestrator.Core;

public enum GoalExplicitDispositionAction { Park, Unpark }

/// <summary>
/// Latest explicit lifecycle transition. A proposal becomes authority only when its goal's
/// row-version CAS commits; competing commands must reload, never compare equal revisions.
/// </summary>
public sealed record GoalExplicitDisposition
{
    public GoalExplicitDisposition(long revision, GoalExplicitDispositionAction action)
    {
        if (revision <= 0) throw new ArgumentOutOfRangeException(nameof(revision));
        if (!Enum.IsDefined(action)) throw new ArgumentOutOfRangeException(nameof(action));
        Revision = revision;
        Action = action;
    }

    public long Revision { get; }
    public GoalExplicitDispositionAction Action { get; }
}
