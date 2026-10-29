using static Mcg.AgentOrchestrator.App.OwnerConsole.OwnerEventFields;
using static Mcg.AgentOrchestrator.App.OwnerConsole.OwnerActivityText;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

// Lifecycle state belongs to one chronological Narrate invocation.
internal sealed class OwnerConductorLifecycleNarration
{
    private bool stopped;
    private bool restarting;

    internal bool TryNarrate(IReadOnlyList<OwnerConductEvent> ordered, int index,
        out string sentence, out string why, out string next, out string act)
    {
        sentence = why = next = "";
        act = "No. You can let the conductor continue.";
        var item = ordered[index];
        var head = Head(item);
        switch (item.EventKind)
        {
            case "loop-handoff":
                if (head is "ACTIVATION_REVERTED" or "ACTIVATION_FAILED_BOTH" or "LOOP_HANDOFF_FAILED")
                {
                    var reason = RestartReason(item);
                    // FAILED_BOTH supplies no evidence that the old process remains usable.
                    if (head == "ACTIVATION_FAILED_BOTH" || head == "LOOP_HANDOFF_FAILED" &&
                        (Field(item, "continuing") != "true" || stopped))
                    {
                        sentence = "Conductor stopped";
                        why = "The conductor could not start the new version (" + reason + ").";
                        next = "Work waits for the conductor to be started.";
                        act = "Yes. Check conductor status and restart it after addressing the failure.";
                    }
                    else
                    {
                        sentence = "Conductor could not switch to the new code; still running the previous version (" + reason + ")";
                        why = "The new version could not be activated.";
                        next = "The previous version continues running.";
                        act = "Yes. Review conductor status and address the startup failure.";
                    }
                }
                break;
            case "loop-stop":
                stopped = true;
                restarting = Field(item, "reason") == "self-relaunch-handoff" ||
                    Field(item, "reason") == "max-duration" && ordered.Skip(index + 1).Any(value =>
                        value.EventKind == "loop-start" || value.EventKind == "loop-handoff" && Head(value) == "ACTIVATION_ADOPTED");
                if (!restarting)
                {
                    sentence = "Conductor stopped";
                    why = "The conductor finished or was stopped.";
                    next = "Work waits until the conductor starts.";
                }
                break;
            case "loop-start":
                if (!restarting && Field(item, "selfCheck") != "true")
                {
                    sentence = "Conductor started";
                    why = "The conductor was started.";
                    next = "It will continue the queued work.";
                }
                restarting = false;
                stopped = false;
                break;
        }
        return sentence.Length > 0;
    }
}
