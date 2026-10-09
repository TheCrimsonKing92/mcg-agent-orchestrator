using Mcg.AgentOrchestrator.Infrastructure;

public sealed class ExtendedLengthPathTests
{
    [Xunit.Fact]
    public void Convert_DrivePath_AddsExtendedPrefix()
    {
        if (!OperatingSystem.IsWindows()) { return; }

        Xunit.Assert.Equal(@"\\?\C:\dir\file.log", ExtendedLengthPath.Convert(@"C:\dir\file.log"));
    }

    [Xunit.Fact]
    public void Convert_UncPath_RewritesServerPrefix()
    {
        if (!OperatingSystem.IsWindows()) { return; }

        Xunit.Assert.Equal(
            @"\\?\UNC\server\share\dir\file.log",
            ExtendedLengthPath.Convert(@"\\server\share\dir\file.log"));
    }

    [Xunit.Fact]
    public void Convert_AlreadyPrefixedPath_IsUnchanged()
    {
        if (!OperatingSystem.IsWindows()) { return; }

        const string path = @"\\?\C:\dir\file.log";
        Xunit.Assert.Equal(path, ExtendedLengthPath.Convert(path));
    }

    [Xunit.Fact]
    public void Convert_NonWindows_ReturnsFullPathUnchanged()
    {
        var path = Path.Combine(Path.GetTempPath(), "file.log");

        Xunit.Assert.Equal(Path.GetFullPath(path), ExtendedLengthPath.Convert(path, isWindows: false));
    }
}
