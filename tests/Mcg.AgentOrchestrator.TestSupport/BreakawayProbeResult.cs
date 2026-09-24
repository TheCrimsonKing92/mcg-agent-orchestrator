public sealed record BreakawayProbeResult(
    BreakawayVerdict Verdict,
    string Reason,
    int Win32ErrorCode)
{
    public bool IsPermitted => Verdict == BreakawayVerdict.Permitted;
}
