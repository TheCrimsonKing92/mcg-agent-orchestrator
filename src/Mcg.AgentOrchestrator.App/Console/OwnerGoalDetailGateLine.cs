namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal static class OwnerGoalDetailGateLine
{
    internal static string Text(OwnerConsoleGateProgress gate)
    {
        var members = gate.Members.Count;
        var text = $"{gate.Kind}, {members} {(members == 1 ? "member" : "members")}";
        if (gate.TotalCount == 0) text += $", running {(int)gate.Elapsed.TotalMinutes}m";
        else
        {
            var failed = gate.Lanes.Count(lane => !lane.IsAttribution &&
                lane.State == OwnerConsoleGateLaneState.Done && lane.Outcome == "failed");
            var estimate = gate.Failing ? "failing" : OwnerConsoleGateFormatter.Remaining(gate.RemainingSeconds) + " left";
            text += $", {gate.DoneCount}/{gate.TotalCount} lanes, {failed} failed, {estimate}";
        }
        return text + " (g opens the gate view)";
    }
}
