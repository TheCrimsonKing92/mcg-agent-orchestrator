using System.Diagnostics;

internal static class ScriptFixtureProcessRunner
{
    internal static void AssertExited(Process process, string failsafeMessage) =>
        AssertExited(
            process,
            failsafeMessage,
            static (candidate, timeout) => candidate.WaitForExit(timeout),
            static candidate => candidate.Kill(entireProcessTree: true));

    internal static void AssertExited(
        Process process,
        string failsafeMessage,
        Func<Process, int, bool> waitForExit,
        Action<Process> killTree)
    {
        var exited = waitForExit(process, 30_000);
        if (!exited)
        {
            try
            {
                killTree(process);
            }
            catch (InvalidOperationException)
            {
                // The child may have exited between the bounded wait and the kill.
            }

            waitForExit(process, 5_000);
        }

        Xunit.Assert.True(exited, failsafeMessage);
    }
}
