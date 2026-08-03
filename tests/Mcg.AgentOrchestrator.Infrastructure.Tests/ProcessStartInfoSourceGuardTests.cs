public sealed class ProcessStartInfoSourceGuardTests
{
    [Fact(DisplayName = "Test process starts opt out of visible console windows")]
    public void TestProcessStartsOptOutOfVisibleConsoleWindows()
    {
        var testRoot = FindTestRoot();
        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(testRoot, "*.cs", SearchOption.AllDirectories))
        {
            if (Path.GetFileName(file).Equals(nameof(ProcessStartInfoSourceGuardTests) + ".cs", StringComparison.Ordinal))
            {
                continue;
            }

            var relativePath = Path.GetRelativePath(testRoot, file);
            var lines = File.ReadAllLines(file);
            for (var index = 0; index < lines.Length; index++)
            {
                if (IntroducesStartProcessCommand(lines[index]))
                {
                    var startProcessEnd = Math.Min(lines.Length, index + 8);
                    var startProcessWindow = string.Join('\n', lines[index..startProcessEnd]);
                    if (!startProcessWindow.Contains("MCG_ALLOW_DEFAULT_WINDOW_SETTINGS_PROBE", StringComparison.Ordinal) &&
                        !startProcessWindow.Contains("-NoNewWindow", StringComparison.Ordinal) &&
                        !startProcessWindow.Contains("-WindowStyle Hidden", StringComparison.Ordinal))
                    {
                        offenders.Add($"{relativePath}:{index + 1} (Start-Process)");
                    }
                }

                if (!IntroducesProcessStartInfo(lines[index]))
                {
                    continue;
                }

                var end = Math.Min(lines.Length, index + 30);
                var sourceWindow = string.Join('\n', lines[index..end]);
                if (sourceWindow.Contains("MCG_ALLOW_DEFAULT_WINDOW_SETTINGS_PROBE", StringComparison.Ordinal))
                {
                    continue;
                }

                if (!sourceWindow.Contains("CreateNoWindow", StringComparison.Ordinal))
                {
                    offenders.Add($"{relativePath}:{index + 1}");
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            "Test-side process starts must set CreateNoWindow=true or request a hidden/no-new PowerShell window. Offenders: " + string.Join(", ", offenders));
    }

    private static bool IntroducesProcessStartInfo(string line)
    {
        return line.Contains("new ProcessStartInfo", StringComparison.Ordinal)
            || line.Contains("ProcessStartInfo]::new", StringComparison.Ordinal)
            || line.Contains("UseShellExecute = false", StringComparison.Ordinal)
            || line.Contains("UseShellExecute = $false", StringComparison.Ordinal);
    }

    private static bool IntroducesStartProcessCommand(string line)
    {
        return line.Contains("Start-Process", StringComparison.Ordinal) &&
            !line.Contains("(\"Start-Process\"", StringComparison.Ordinal);
    }

    private static string FindTestRoot([System.Runtime.CompilerServices.CallerFilePath] string sourceFilePath = "")
    {
        var sourceDirectory = Path.GetDirectoryName(sourceFilePath);
        if (Directory.Exists(sourceDirectory) &&
            Path.GetFileName(sourceDirectory).Equals("Mcg.AgentOrchestrator.Infrastructure.Tests", StringComparison.Ordinal))
        {
            return sourceDirectory;
        }

        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "tests", "Mcg.AgentOrchestrator.Infrastructure.Tests");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            "Could not locate tests/Mcg.AgentOrchestrator.Infrastructure.Tests. " +
            "The source guard needs repository sources; CallerFilePath did not resolve to a checked-out test source directory.");
    }
}
