using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

public sealed class LineEndingPolicySourceGuardTests
{
    [Fact]
    public void Attributes_RepositoryPolicy_DeclaresOrderedRules()
    {
        var root = VerifiedRepositoryRoot.Find();
        var lines = File.ReadAllLines(Path.Combine(root, ".gitattributes"))
            .Where(line => !string.IsNullOrWhiteSpace(line) && !line.TrimStart().StartsWith('#'))
            .ToArray();
        Assert.Equal(new[] { "* text=auto eol=lf", "*.cmd text eol=crlf", "*.bat text eol=crlf" }, lines.Take(3));
        Assert.Contains("tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Fixtures/ReviewerFindingsParse/ae54b5eb-e9ea0560-20260905170025.out.txt -text", lines);
        Assert.Contains("tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Fixtures/ReviewerFindingsParse/b2f52d39-0d99c73d-20260905153941.out.txt -text", lines);
    }

    [Fact]
    public async Task Index_TrackedText_UsesLfUnlessExempt()
    {
        var root = VerifiedRepositoryRoot.Find();
        var output = await ReadIndexEntries(root);
        Assert.True(output.Split('\0', StringSplitOptions.RemoveEmptyEntries).Length > 0,
            $"git ls-files --eol -z at '{root}' returned no tracked index entries.");
        var violations = FindViolations(output);
        Assert.True(violations.Length == 0,
            $"git ls-files --eol -z at '{root}' found non-LF or malformed index entries: {string.Join(", ", violations)}");
    }

    [Fact]
    public void IndexParser_MixedAndCrlfEntries_RejectsOnlyNonExemptText()
    {
        var output = "i/lf    w/crlf  attr/text=auto eol=lf\tclean.cs\0" +
            "i/crlf  w/crlf  attr/text eol=crlf\tbad.cmd\0" +
            "i/mixed w/mixed attr/text=auto eol=lf\tbad.cs\0" +
            "i/mixed w/mixed attr/-text\tfixture.txt\0" +
            "i/-text w/-text attr/text=auto eol=lf\tbinary.png\0" +
            "i/none  w/none  attr/text=auto eol=lf\tempty.txt\0";
        Assert.Equal(new[] { "bad.cmd (i/crlf)", "bad.cs (i/mixed)" }, FindViolations(output));
        Assert.Single(FindViolations("malformed\0"));
        Assert.Single(FindViolations("i/lf w/lf attr/text=auto eol=lf\tunterminated.cs"));
    }

    private static string[] FindViolations(string output)
    {
        var violations = new List<string>();
        if (!output.EndsWith('\0')) violations.Add("Missing NUL record terminator");
        foreach (var record in output.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            var match = Regex.Match(record, @"^i/(\S+)\s+w/\S+\s+attr/([^\t]*)\t(.+)$", RegexOptions.Singleline);
            if (!match.Success) { violations.Add($"Malformed record: {record}"); continue; }
            var index = match.Groups[1].Value;
            var attributes = match.Groups[2].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (index is "-text" or "none" || attributes.Contains("-text")) continue;
            if (index != "lf") violations.Add($"{match.Groups[3].Value} (i/{index})");
        }
        return violations.ToArray();
    }

    private static async Task<string> ReadIndexEntries(string root)
    {
        // RunGitProbe bounds stdout to 4 KiB; this guard must inspect the entire index.
        var start = new ProcessStartInfo(OperatingSystem.IsWindows() ? "git.exe" : "git")
        {
            WorkingDirectory = root,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        start.Environment.Clear();
        foreach (var name in new[] { "PATH", "PATHEXT", "SystemRoot", "WINDIR", "COMSPEC", "TEMP", "TMP" })
            if (Environment.GetEnvironmentVariable(name) is { } value) start.Environment[name] = value;
        start.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        start.Environment["GIT_CONFIG_GLOBAL"] = OperatingSystem.IsWindows() ? "NUL" : "/dev/null";
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        start.Environment["GIT_OPTIONAL_LOCKS"] = "0";
        foreach (var argument in new[] { "-c", "core.fsmonitor=false", "ls-files", "--eol", "-z" })
            start.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = start };
        var invocation = $"git ls-files --eol -z at '{root}'";
        try
        {
            Assert.True(process.Start(), $"Could not start {invocation}");
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            Assert.Fail($"Could not start {invocation}: {exception.Message}");
        }
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        try
        {
            // Hang detector only: git must exit and both redirected streams must close.
            await Task.WhenAll(process.WaitForExitAsync(), output, error).WaitAsync(TimeSpan.FromSeconds(60));
        }
        catch (TimeoutException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            Assert.Fail($"{invocation} did not exit and close its output streams before the hang failsafe.");
        }
        Assert.True(process.ExitCode == 0, $"{invocation} exited {process.ExitCode}: {await error}");
        return await output;
    }
}
