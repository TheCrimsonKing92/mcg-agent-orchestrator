public sealed class ProcessStartInfoSourceGuardLexicalTests
{
    [Fact]
    public void CSharpLaunchTextInLiteralsAndCommentsIsIgnored()
    {
        var samples = new[]
        {
            new[] { "var sample = \"new ProcessStartInfo; UseShellExecute = false\";" },
            new[] { "var sample = @\"new ProcessStartInfo; UseShellExecute = false\";" },
            new[] { "var sample = $\"new ProcessStartInfo; UseShellExecute = false\";" },
            new[] { "var sample = $@\"new ProcessStartInfo; UseShellExecute = false\";" },
            new[] { "var sample = @$\"new ProcessStartInfo; UseShellExecute = false\";" },
            new[] { "var sample = \"\"\"", "new ProcessStartInfo; UseShellExecute = false", "\"\"\";" },
            new[] { "var sample = $$\"\"\"", "new ProcessStartInfo; UseShellExecute = false", "\"\"\";" },
            new[] { "// new ProcessStartInfo; UseShellExecute = false" },
            new[] { "/* new ProcessStartInfo;", "UseShellExecute = false */" },
            new[] { "/*/ new ProcessStartInfo */" }
        };

        foreach (var lines in samples)
            Assert.Empty(Offenders(lines));
    }

    [Fact]
    public void CSharpLaunchesInCodeKeepTheOriginalWindowAndLineNumbers()
    {
        Assert.Equal(new[] { "sample.cs:1", "sample.cs:2" }, Offenders(
            "var psi = new ProcessStartInfo(\"tool.exe\");",
            "psi.UseShellExecute = false;"));
        Assert.Equal(new[] { "sample.cs:1" }, Offenders(
            "var example = \"new ProcessStartInfo\"; var psi = new ProcessStartInfo(\"tool.exe\");"));
        Assert.Empty(Offenders("var psi = new ProcessStartInfo(\"tool.exe\");", "psi.CreateNoWindow = true;"));
        Assert.Empty(Offenders("var psi = new ProcessStartInfo(\"tool.exe\");", "// MCG_ALLOW_DEFAULT_WINDOW_SETTINGS_PROBE"));
    }

    [Fact]
    public void EmbeddedPowerShellStillUsesRawTextMatchesAndOriginalWindows()
    {
        var constructor = "ProcessStartInfo]::" + "new";
        var shellFlag = "UseShellExecute = $" + "false";
        var startCommand = "Start-" + "Process";

        Assert.Equal(new[] { "sample.cs:1" }, Offenders($"var script = \"{constructor}\";"));
        Assert.Equal(new[] { "sample.cs:1" }, Offenders($"var script = \"{shellFlag}\";"));
        Assert.Equal(new[] { "sample.cs:2" }, Offenders("var script = \"\"\"", constructor, "\"\"\";"));
        Assert.Equal(new[] { $"sample.cs:1 ({startCommand})" }, Offenders($"var script = \"{startCommand} tool.exe\";"));

        Assert.Empty(Offenders($"var script = \"{constructor}\";", "var optOut = \"CreateNoWindow\";"));
        Assert.Empty(Offenders($"var script = \"{shellFlag}\";", "var optOut = \"CreateNoWindow\";"));
        Assert.Empty(Offenders($"var script = \"{startCommand} tool.exe\";", "var optOut = \"-WindowStyle Hidden\";"));
        Assert.Empty(Offenders($"var script = \"{startCommand} tool.exe\";", "var optOut = \"-NoNewWindow\";"));
        Assert.Empty(Offenders($"var script = \"{startCommand}\";"));
    }

    [Fact]
    public void QuotesAndRawStringCloseDoNotHideFollowingCode()
    {
        Assert.Equal(new[] { "sample.cs:1" }, Offenders(
            "var quote = '\"'; var psi = new ProcessStartInfo(\"tool.exe\");"));
        Assert.Equal(new[] { "sample.cs:2" }, Offenders(
            "var sample = \"escaped \\\" quote, new ProcessStartInfo\";",
            "var psi = new ProcessStartInfo(\"tool.exe\");"));
        Assert.Equal(new[] { "sample.cs:3" }, Offenders(
            "var sample = @\"doubled \"\"",
            "new ProcessStartInfo\";",
            "var psi = new ProcessStartInfo(\"tool.exe\");"));
        Assert.Equal(new[] { "sample.cs:4" }, Offenders(
            "var sample = \"\"\"",
            "new ProcessStartInfo",
            "\"\"\";",
            "var psi = new ProcessStartInfo(\"tool.exe\");"));
    }

    [Fact]
    public void UnterminatedMultilineConstructsFailLoudly()
    {
        Assert.Throws<InvalidOperationException>(() => Offenders("/* unclosed comment"));
        Assert.Throws<InvalidOperationException>(() => Offenders("var sample = @\"unclosed verbatim"));
        Assert.Throws<InvalidOperationException>(() => Offenders("var sample = \"\"\"", "unclosed raw"));
    }

    [Fact]
    public void InterpolationExpressionsRemainCode()
    {
        Assert.Equal(new[] { "sample.cs:1" }, Offenders(
            "var sample = $\"text {new ProcessStartInfo(\"tool.exe\")}\";"));
        Assert.Equal(new[] { "sample.cs:1" }, Offenders(
            "var sample = $$\"\"\"text {{new ProcessStartInfo(\"tool.exe\")}}\"\"\";"));
        Assert.Empty(Offenders("var sample = $\"{{new ProcessStartInfo}}\";"));
    }

    private static string[] Offenders(params string[] lines) =>
        ProcessStartInfoSourceGuardTests.FindOffenders("sample.cs", lines).ToArray();
}
