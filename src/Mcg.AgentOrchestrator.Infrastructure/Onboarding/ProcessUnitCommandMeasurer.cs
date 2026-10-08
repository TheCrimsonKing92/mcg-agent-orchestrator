using System.Diagnostics;
using System.Text;

namespace Mcg.AgentOrchestrator.Infrastructure;

/// <summary>Executes dotnet directly, with bounded lifetime and no shell interpretation.</summary>
public sealed class ProcessUnitCommandMeasurer : IUnitCommandMeasurer
{
    private readonly TimeSpan _timeout;

    public ProcessUnitCommandMeasurer(TimeSpan? timeout = null)
    {
        _timeout = timeout ?? TimeSpan.FromMinutes(30);
        if (_timeout <= TimeSpan.Zero || _timeout.TotalMilliseconds > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(timeout));
    }

    public UnitCommandMeasurementResult Measure(string repositoryRoot, string unitId, UnitCommandKinds kind, string command)
    {
        var tokens = Tokenize(command);
        if (tokens.Count == 0 || tokens[0] != "dotnet")
            return UnitCommandMeasurementResult.Failed("Only learned dotnet commands can be measured.");
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = repositoryRoot,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var token in tokens.Skip(1)) start.ArgumentList.Add(token);
        using var process = new Process { StartInfo = start };
        var clock = Stopwatch.StartNew();
        try
        {
            if (!process.Start())
                return UnitCommandMeasurementResult.Failed("The dotnet process did not start.");
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or IOException)
        {
            return UnitCommandMeasurementResult.Failed($"Could not start dotnet: {exception.Message}");
        }
        process.StandardInput.Close();
        var stdout = process.StandardOutput.BaseStream.CopyToAsync(Stream.Null);
        var stderr = process.StandardError.BaseStream.CopyToAsync(Stream.Null);
        var exited = process.WaitForExit((int)_timeout.TotalMilliseconds);
        if (!exited)
        {
            try { process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { } // The process may have exited after the timeout observation.
            process.WaitForExit();
        }
        Task.WhenAll(stdout, stderr).GetAwaiter().GetResult();
        clock.Stop();
        return !exited ? UnitCommandMeasurementResult.Failed("The dotnet command timed out.")
            : process.ExitCode != 0 ? UnitCommandMeasurementResult.Failed($"The dotnet command exited with code {process.ExitCode}.")
            : UnitCommandMeasurementResult.Succeeded(clock.Elapsed.TotalSeconds);
    }

    private static List<string> Tokenize(string command)
    {
        var tokens = new List<string>();
        var token = new StringBuilder();
        var quoted = false;
        foreach (var character in command)
        {
            if (character == '"') quoted = !quoted;
            else if (char.IsWhiteSpace(character) && !quoted)
            {
                if (token.Length > 0) { tokens.Add(token.ToString()); token.Clear(); }
            }
            else token.Append(character);
        }
        if (quoted) throw new ArgumentException("The learned command contains an unmatched quote.", nameof(command));
        if (token.Length > 0) tokens.Add(token.ToString());
        return tokens;
    }
}
