public static class ConductorLoopHandoffTestConditions
{
    public static bool IsWindowsBreakawayPermitted =>
        OperatingSystem.IsWindows() && BreakawayJobProbe.CanCreateBreakawayChild().IsPermitted;
}
