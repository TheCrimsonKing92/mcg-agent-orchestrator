using System.Diagnostics;

namespace Mcg.AgentOrchestrator.Infrastructure;

// Owns shell quoting and platform-specific setup after the child environment is configured.
internal static class AcceptanceShellProcessStartInfo
{
    internal static ProcessStartInfo ForCheck(
        string[] arguments,
        string workingDirectory,
        string? stdoutRedirectTarget,
        string? stderrRedirectTarget,
        bool forceUtf8ConsoleOutput,
        AcceptanceProcessRequest request,
        out string? resolvedCommandPath) =>
        Build(arguments, workingDirectory, stdoutRedirectTarget, stderrRedirectTarget,
            forceUtf8ConsoleOutput,
            environment =>
            {
                AcceptanceHermeticEnvironment.ConfigureHermeticVerificationEnvironment(
                    environment, workingDirectory, request.HeartbeatContext?.BuildEnvironmentRoot);
                if (string.IsNullOrWhiteSpace(request.GateInvocationId))
                    TempRootApparatusLossReceiptStore.ApplyCurrentScope(environment);
                else
                    TempRootApparatusLossReceiptStore.ApplyScope(environment, request.GateInvocationId, request.ApparatusReceiptPath);
                if (request.ChildEnvironmentOverrides is { } overrides)
                {
                    foreach (var pair in overrides)
                    {
                        if (pair.Value is null) environment.Remove(pair.Key);
                        else environment[pair.Key] = pair.Value;
                    }
                }
            },
            request.ResolveBareCommand, out resolvedCommandPath);

    private static string BuildShellCommand(string[] arguments, Func<string, string> quote) =>
        string.Join(' ', arguments.Select(quote));

    private static string QuoteForCmd(string value) =>
        $"\"{value.Replace("\"", "\\\"", StringComparison.Ordinal)}\"";

    private static string QuoteForPosix(string value) =>
        $"'{value.Replace("'", "'\\''", StringComparison.Ordinal)}'";

    internal static ProcessStartInfo Build(
        string[] arguments,
        string workingDirectory,
        string? stdoutRedirectTarget,
        string? stderrRedirectTarget,
        bool forceUtf8ConsoleOutput,
        Action<IDictionary<string, string?>>? configureEnvironment,
        bool resolveBareCommand,
        out string? resolvedCommandPath)
    {
        var startInfo = new ProcessStartInfo
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory
        };
        configureEnvironment?.Invoke(startInfo.Environment);
        resolvedCommandPath = null;

        if (OperatingSystem.IsWindows())
        {
            if (string.IsNullOrWhiteSpace(stdoutRedirectTarget) != string.IsNullOrWhiteSpace(stderrRedirectTarget))
            {
                throw new ArgumentException("Both capture redirection targets must be provided together.");
            }

            startInfo.FileName = "cmd.exe";
            if (resolveBareCommand && arguments.Length > 0)
            {
                resolvedCommandPath = AcceptanceBareCommandResolver.Resolve(
                    arguments[0], startInfo.Environment, workingDirectory);
                if (resolvedCommandPath is not null)
                {
                    arguments = (string[])arguments.Clone();
                    arguments[0] = resolvedCommandPath;
                }
            }
            var command = BuildShellCommand(AcceptanceMtpFilterResponseFile.Apply(arguments), QuoteForCmd);
            if (forceUtf8ConsoleOutput)
            {
                // MTP formats theory arguments before writing them. Under an OEM console code page,
                // Windows best-fit conversion irreversibly changes CJK and combining characters before
                // capture, so decoding the resulting bytes cannot repair the discovery identity.
                // Group the preflight with the child command so owned stdout/stderr redirections
                // are opened before chcp runs. A failed preflight then returns its non-zero exit
                // code and captured stderr instead of leaving the named-pipe readers unconnected.
                command = $"(chcp 65001 > nul && {command})";
            }
            if (!string.IsNullOrWhiteSpace(stdoutRedirectTarget))
            {
                command = $"{command} > {QuoteForCmd(stdoutRedirectTarget)} 2> {QuoteForCmd(stderrRedirectTarget!)}";
            }
            else
            {
                // Retain the managed-pipe form for focused capture tests and non-owned callers.
                startInfo.RedirectStandardOutput = true;
                startInfo.RedirectStandardError = true;
            }

            // cmd /c strips one surrounding quote pair, so wrap the whole command once.
            startInfo.Arguments = $"/c \"{command}\"";
            GoalAcceptanceVerifier.ThrowIfCmdCommandLineTooLong(startInfo.Arguments, arguments);
        }
        else
        {
            startInfo.RedirectStandardOutput = true;
            startInfo.RedirectStandardError = true;
            startInfo.FileName = "/bin/sh";
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add(BuildShellCommand(arguments, QuoteForPosix));
        }

        return startInfo;
    }
}
