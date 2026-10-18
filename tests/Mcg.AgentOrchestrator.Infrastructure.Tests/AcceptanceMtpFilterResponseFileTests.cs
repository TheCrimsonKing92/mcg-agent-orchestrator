using System.Security.Cryptography;
using System.Text;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: only immutable argument arrays and uniquely owned temporary directories.
public sealed class AcceptanceMtpFilterResponseFileTests
{
    private const string Stdout = @"\\.\pipe\response-file-out";
    private const string Stderr = @"\\.\pipe\response-file-err";

    [Fact]
    public void Plan_LongFilters_PreservesOrderAndUsesStableContentHash()
    {
        string[] pairs = ["--filter-class", "*Included*", "--filter-method", "*Method*",
            "--filter-not-method", "*Excluded*", "--filter-not-trait", "Category=HostIntegration",
            .. LongExclusions()];
        string[] arguments = ["dotnet", "x.dll", "--results-directory", "D/", "--list-tests", "json", .. pairs];
        var spill = AcceptanceMtpFilterResponseFile.Plan(arguments);
        var text = string.Join('\n', pairs);
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16];

        Assert.True(QuotedLength(arguments) > 4096);
        Assert.Equal($"D/filter-{hash}.rsp", spill.ResponseFilePath);
        Assert.Equal(pairs, spill.ResponseFileText!.Split('\n'));
        Assert.Equal(new[] { "dotnet", "x.dll", "--results-directory", "D/", "--list-tests", "json",
            "@" + spill.ResponseFilePath }, spill.Arguments);
        var repeated = AcceptanceMtpFilterResponseFile.Plan(arguments.ToArray());
        Assert.Equal(spill.ResponseFilePath, repeated.ResponseFilePath);
        Assert.Equal(spill.ResponseFileText, repeated.ResponseFileText);
    }

    [Theory]
    [InlineData(4095)]
    [InlineData(4096)]
    [InlineData(4097)]
    public void Plan_QuotedLengthBoundary_SpillsOnlyAboveBudget(int length)
    {
        var arguments = AtQuotedLength(length, "D");
        Assert.Equal(length, QuotedLength(arguments));
        var spill = AcceptanceMtpFilterResponseFile.Plan(arguments);
        if (length > 4096) Assert.NotNull(spill.ResponseFilePath);
        else AssertUnchanged(arguments, spill);
    }

    [Fact]
    public void Plan_IneligibleCommands_KeepOriginalArguments()
    {
        var pairs = LongExclusions();
        string[][] cases = [
            ["git", "diff", "--results-directory", "D", .. pairs],
            ["dotnet", "build", "--results-directory", "D", .. pairs],
            ["dotnet", "x.dll", .. pairs],
            ["dotnet", "x.dll", "--results-directory", "D", new string('x', 5000)],
            ["dotnet", "x.dll", "--results-directory", "D", new string('x', 5000), "--filter-class"],
            ["dotnet", "x.dll", "--results-directory", "D", "--FILTER-CLASS", new string('x', 5000)],
            ["dotnet", "x.dll", "--results-directory", "D", "--filter-class=" + new string('x', 5000)]
        ];
        foreach (var arguments in cases)
        {
            Assert.True(QuotedLength(arguments) > 4096);
            AssertUnchanged(arguments, AcceptanceMtpFilterResponseFile.Plan(arguments));
        }
    }

    [Theory]
    [InlineData("bad value")]
    [InlineData("bad\tvalue")]
    [InlineData("bad\"value")]
    [InlineData("#comment")]
    [InlineData("@nested")]
    public void Plan_UnsafeSpilledValue_FailsWithOffendingValue(string value)
    {
        string[] arguments = ["dotnet", "x.dll", "--results-directory", "D",
            "--filter-class", value, .. LongExclusions()];
        var error = Assert.Throws<InvalidDataException>(() => AcceptanceMtpFilterResponseFile.Plan(arguments));
        Assert.Contains(value, error.Message, StringComparison.Ordinal);
        var applyError = Assert.Throws<InvalidDataException>(() => AcceptanceMtpFilterResponseFile.Apply(arguments));
        Assert.Equal(error.Message, applyError.Message);
    }

    [Fact]
    public void Plan_FullDotnetHostPath_RecognizesManagedInvocation()
    {
        string[] arguments = [Path.Combine(Path.GetTempPath(), "host", "dotnet.exe"), "x.DLL",
            "--results-directory", "D", .. LongExclusions()];
        Assert.NotNull(AcceptanceMtpFilterResponseFile.Plan(arguments).ResponseFilePath);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Apply_RelativeResultsDirectory_KeepsArgumentsAndFilesystem(bool empty)
    {
        var relative = empty ? "" : $"response-file-{Guid.NewGuid():N}";
        string[] arguments = ["dotnet", "x.dll", "--results-directory", relative, .. LongExclusions()];
        Assert.NotNull(AcceptanceMtpFilterResponseFile.Plan(arguments).ResponseFilePath);
        Assert.Same(arguments, AcceptanceMtpFilterResponseFile.Apply(arguments));
        Assert.False(Directory.Exists(relative));
    }

    [Fact]
    public void Launcher_LongFilters_WritesOneBomFreeResponseFileWithinBudget()
    {
        if (!OperatingSystem.IsWindows()) return;
        var directory = Path.Combine(Path.GetTempPath(), $"mtp-response-{Guid.NewGuid():N}");
        try
        {
            var pairs = LongExclusions();
            Assert.True(string.Join(' ', pairs).Length >= 20000);
            string[] arguments = ["dotnet", "x.dll", "--results-directory", directory, .. pairs];
            var spill = AcceptanceMtpFilterResponseFile.Plan(arguments);
            var start = Launch(arguments);
            Assert.Equal("cmd.exe", start.FileName);
            Assert.True(start.Arguments.Length <= 7935);
            Assert.Contains("\"@" + spill.ResponseFilePath, start.Arguments, StringComparison.Ordinal);
            var expected = Encoding.UTF8.GetBytes(spill.ResponseFileText!);
            Assert.Equal(expected, File.ReadAllBytes(spill.ResponseFilePath!));

            Assert.Equal(start.Arguments, Launch(arguments).Arguments);
            Assert.Single(Directory.GetFiles(directory, "*.rsp"));
            Assert.Equal(expected, File.ReadAllBytes(spill.ResponseFilePath!));
            // A stale/corrupted artifact is repaired rather than silently reused.
            File.WriteAllText(spill.ResponseFilePath!, "corrupt");
            AcceptanceMtpFilterResponseFile.Apply(arguments);
            Assert.Equal(expected, File.ReadAllBytes(spill.ResponseFilePath!));
            Assert.Single(Directory.GetFiles(directory));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Launcher_IneligibleLongCommand_PreservesCapDiagnostics(bool managed)
    {
        if (!OperatingSystem.IsWindows()) return;
        string[] arguments = managed
            ? ["dotnet", "x.dll", "--filter-not-class", new string('x', 20000)]
            : ["git", "diff", new string('x', 8200)];
        var expectedLine = Compose(arguments);
        var error = Assert.Throws<CommandLineTooLongException>(() => Launch(arguments));
        Assert.Equal(expectedLine.Length, error.Length);
        Assert.Equal(8191, error.Limit);
        Assert.Equal(arguments[0], error.FirstArgument);
        Assert.Equal($"Windows cmd.exe command line is {expectedLine.Length} characters, which exceeds the cmd.exe limit of 8191 characters; first argument: {arguments[0]}", error.Message);
    }

    [Fact]
    public void Launcher_ShortManagedCommand_KeepsExactLineAndWritesNoResponseFile()
    {
        if (!OperatingSystem.IsWindows()) return;
        var directory = Path.Combine(Path.GetTempPath(), $"mtp-short-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var arguments = AtQuotedLength(2000, directory);
            Assert.Equal(2000, QuotedLength(arguments));
            Assert.Same(arguments, AcceptanceMtpFilterResponseFile.Apply(arguments));
            Assert.Equal(Compose(arguments), Launch(arguments).Arguments);
            Assert.Empty(Directory.GetFiles(directory, "*.rsp"));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static string[] LongExclusions() => Enumerable.Range(0, 600)
        .SelectMany(index => new[] { "--filter-not-class", $"*NoSuchLaneClass{index:D4}*" }).ToArray();

    private static int QuotedLength(string[] arguments) =>
        arguments.Sum(argument => argument.Length + 2) + arguments.Length - 1;

    private static string[] AtQuotedLength(int length, string directory)
    {
        string[] arguments = ["dotnet", "x.dll", "--results-directory", directory, "--filter-class", ""];
        arguments[^1] = new string('x', length - QuotedLength(arguments));
        return arguments;
    }

    private static void AssertUnchanged(string[] arguments, AcceptanceMtpFilterSpill spill)
    {
        Assert.Equal(arguments, spill.Arguments);
        Assert.Null(spill.ResponseFilePath);
        Assert.Null(spill.ResponseFileText);
    }

    private static System.Diagnostics.ProcessStartInfo Launch(string[] arguments) =>
        GoalAcceptanceVerifier.BuildAcceptanceProcessStartInfo(arguments, Path.GetTempPath(), Stdout, Stderr,
            forceUtf8ConsoleOutput: true);

    private static string Compose(string[] arguments) =>
        $"/c \"(chcp 65001 > nul && {string.Join(' ', arguments.Select(argument => $"\"{argument}\""))}) > \"{Stdout}\" 2> \"{Stderr}\"\"";
}
