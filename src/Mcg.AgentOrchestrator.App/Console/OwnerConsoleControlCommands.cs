namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal sealed class OwnerConsoleControlCommands(
    IOwnerConsoleConductor? conductor,
    IOwnerConsoleDigestReport? digestReport,
    IOwnerConsoleOutput output)
{
    private const string ConductorUsage = "usage: conductor start [--clear-stop] | conductor stop [--yes] | conductor status";
    private const string DetachWarning = "This is a detach, not a drain: live workers are detached without waiting for them to finish.";

    internal void HandleConductor(string line)
    {
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length is < 2 or > 3 ||
            !(parts[1].Equals("status", StringComparison.OrdinalIgnoreCase) && parts.Length == 2 ||
              parts[1].Equals("start", StringComparison.OrdinalIgnoreCase) &&
                  (parts.Length == 2 || parts[2].Equals("--clear-stop", StringComparison.OrdinalIgnoreCase)) ||
              parts[1].Equals("stop", StringComparison.OrdinalIgnoreCase) &&
                  (parts.Length == 2 || parts[2].Equals("--yes", StringComparison.OrdinalIgnoreCase))))
        {
            output.WriteLine(ConductorUsage);
            return;
        }
        var verb = parts[1].ToLowerInvariant();
        if (verb == "stop" && parts.Length == 2)
        {
            output.WriteLine(DetachWarning);
            output.WriteLine("repeat as: conductor stop --yes");
            return;
        }
        if (conductor is null)
        {
            output.WriteLine("conductor control unavailable in this session");
            return;
        }
        var args = verb == "start" && parts.Length == 3
            ? new[] { "conductor", "start", "--clear-stop" }
            : new[] { "conductor", verb };
        using var standard = new StringWriter();
        using var error = new StringWriter();
        conductor.Run(args, standard, error);
        Echo(standard.ToString());
        Echo(error.ToString());
    }

    internal void HandleDigest(string line)
    {
        if (line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length != 1)
        {
            output.WriteLine("usage: digest");
            return;
        }
        if (digestReport is null)
        {
            output.WriteLine("digest unavailable in this session");
            return;
        }
        using var writer = new StringWriter();
        digestReport.Run(writer);
        Echo(writer.ToString());
    }

    private void Echo(string content)
    {
        using var reader = new StringReader(content);
        while (reader.ReadLine() is { } line) output.WriteLine(line);
    }
}
