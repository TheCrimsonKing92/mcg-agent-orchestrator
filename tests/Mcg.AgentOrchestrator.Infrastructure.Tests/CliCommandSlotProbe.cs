internal sealed class CliCommandSlotProbe(HostCapacityTestBudget budget) : CliCommandTestBase
{
    internal TaskCompletionSource AcquisitionRequested { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal int AcquisitionRequests => acquisitionRequests;
    private int acquisitionRequests;

    private protected override HostCapacityTestBudget CapacityBudget
    {
        get
        {
            Interlocked.Increment(ref acquisitionRequests);
            AcquisitionRequested.TrySetResult();
            return budget;
        }
    }

    internal int PureBody() => CountLinesContaining("alpha\nbeta", "alpha");

    internal string FirstHelper() => RunGitOutput(Path.GetTempPath(), "--version");

    internal void SecondHelper() => RunGit(Path.GetTempPath(), "--version");
}
