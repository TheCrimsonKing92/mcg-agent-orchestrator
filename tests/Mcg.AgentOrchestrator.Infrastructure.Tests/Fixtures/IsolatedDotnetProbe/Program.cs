using System.Diagnostics;

if (args.Length == 2 && args[0].Equals("spawn-descendant", StringComparison.Ordinal))
{
    if (!OperatingSystem.IsWindows())
    {
        return 3;
    }

    var windowsDirectory = Environment.GetEnvironmentVariable("WINDIR");
    if (string.IsNullOrWhiteSpace(windowsDirectory))
    {
        return 4;
    }

    var startInfo = new ProcessStartInfo
    {
        FileName = Path.Combine(windowsDirectory, "System32", "ping.exe"),
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true
    };
    startInfo.ArgumentList.Add("-n");
    startInfo.ArgumentList.Add("121");
    startInfo.ArgumentList.Add("127.0.0.1");
    using var child = Process.Start(startInfo);
    if (child is null)
    {
        return 5;
    }

    var pidTempPath = args[1] + $".{Environment.ProcessId}.tmp";
    File.WriteAllText(
        pidTempPath,
        child.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
    File.Move(pidTempPath, args[1], overwrite: true);
    var stdoutDrain = child.StandardOutput.ReadToEndAsync();
    var stderrDrain = child.StandardError.ReadToEndAsync();
    await child.WaitForExitAsync();
    await Task.WhenAll(stdoutDrain, stderrDrain);
    return child.ExitCode;
}

var receiptPath = Environment.GetEnvironmentVariable("MCG_ISOLATED_DOTNET_MTP_PROBE_PATH");
if (string.IsNullOrWhiteSpace(receiptPath))
{
    return 2;
}

File.WriteAllLines(receiptPath, args);
return int.TryParse(
    Environment.GetEnvironmentVariable("MCG_ISOLATED_DOTNET_MTP_PROBE_EXIT_CODE"),
    out var exitCode)
    ? exitCode
    : 0;
