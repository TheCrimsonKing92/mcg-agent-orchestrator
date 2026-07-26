using System.Diagnostics;

// GUI-subsystem relauncher for scheduled tasks that must never show a window.
// Console hosts (pwsh, cmd) flash a conhost window even with -WindowStyle
// Hidden because the console is created before the flag is honored; a WinExe
// parent with CreateNoWindow suppresses the console entirely.
if (args.Length == 0)
{
    return 2;
}

var startInfo = new ProcessStartInfo
{
    FileName = args[0],
    UseShellExecute = false,
    CreateNoWindow = true,
    WindowStyle = ProcessWindowStyle.Hidden
};
for (var index = 1; index < args.Length; index++)
{
    startInfo.ArgumentList.Add(args[index]);
}

using var child = Process.Start(startInfo);
if (child is null)
{
    return 3;
}

// Wait and propagate the exit code so Task Scheduler's execution time limit
// and LastTaskResult still reflect the real child process.
child.WaitForExit();
return child.ExitCode;
