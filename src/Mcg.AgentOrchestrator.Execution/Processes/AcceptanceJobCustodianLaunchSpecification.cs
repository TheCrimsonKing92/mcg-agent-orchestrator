using System.Diagnostics;

namespace Mcg.AgentOrchestrator.Infrastructure;

// App supplies its bound runtime closure; Execution never discovers an application or PATH host.
internal sealed class AcceptanceJobCustodianLaunchSpecification
{
    private readonly string _executable;
    private readonly string[] _arguments;
    private readonly string _workingDirectory;
    private readonly KeyValuePair<string, string>[] _environment;
    internal AcceptanceJobCustodianLaunchSpecification(string executable, IEnumerable<string> arguments,
        string workingDirectory, IReadOnlyDictionary<string, string>? environment = null)
    {
        if (!Path.IsPathFullyQualified(executable) || !File.Exists(executable) ||
            !Path.IsPathFullyQualified(workingDirectory) || !Directory.Exists(workingDirectory))
            throw new InvalidOperationException("Acceptance custody requires an absolute existing host and workspace.");
        _executable = executable;
        _arguments = arguments.ToArray();
        _workingDirectory = workingDirectory;
        _environment = environment?.ToArray() ?? [];
    }

    internal ProcessStartInfo CreateStartInfo()
    {
        var info = new ProcessStartInfo(_executable)
        {
            WorkingDirectory = _workingDirectory, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var argument in _arguments) info.ArgumentList.Add(argument);
        foreach (var (name, value) in _environment) info.Environment[name] = value;
        return info;
    }
}
