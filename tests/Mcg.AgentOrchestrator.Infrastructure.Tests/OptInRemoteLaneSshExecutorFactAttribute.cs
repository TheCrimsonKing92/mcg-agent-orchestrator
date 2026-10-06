internal sealed class OptInRemoteLaneSshExecutorFactAttribute : Xunit.FactAttribute
{
    public OptInRemoteLaneSshExecutorFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MCG_REMOTE_LANE_SSH_EXECUTOR")))
            Skip = "Set MCG_REMOTE_LANE_SSH_EXECUTOR to the admin alias of a configured ssh executor.";
    }
}
