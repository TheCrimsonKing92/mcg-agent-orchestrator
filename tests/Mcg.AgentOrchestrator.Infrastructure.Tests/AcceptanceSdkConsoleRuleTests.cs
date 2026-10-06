using Mcg.AgentOrchestrator.Infrastructure;

public sealed class AcceptanceSdkConsoleRuleTests
{
    public static TheoryData<string[]> SdkCommands => new()
    {
        new[] { "dotnet", "build", "x.csproj" },
        new[] { "dotnet", "msbuild", "-version" },
        new[] { "dotnet", "test", "x.csproj" },
        new[] { "dotnet", "restore" },
        new[] { "dotnet", "--version" },
        new[] { @"C:\Program Files\dotnet\dotnet.exe", "build" },
        new[] { "DOTNET.EXE", "build" },
        new[] { "/usr/bin/dotnet", "build" },
        new[] { "dotnet", "--diagnostics", "build" }
    };

    [Theory]
    [MemberData(nameof(SdkCommands))]
    public void SdkCommandsRequireOwnConsole(string[] arguments) =>
        Assert.True(AcceptanceSdkConsoleRule.RequiresOwnConsole(arguments));

    public static TheoryData<string[]> OtherCommands => new()
    {
        new[] { "dotnet", "Sample.Tests.dll", "--filter", "A" },
        new[] { "dotnet", "exec", "Sample.Tests.dll" },
        new[] { "git", "status" },
        new[] { "cmd", "/c", "echo" },
        Array.Empty<string>(),
        new[] { "dotnet", "--diagnostics", @"tests\Sample.Tests.DLL" },
        new[] { "dotnet", "--diagnostics", "EXEC", "Sample.Tests.dll" },
        new[] { "fake-dotnet.cmd", "build" }
    };

    [Theory]
    [MemberData(nameof(OtherCommands))]
    public void ManagedHostsAndOtherExecutablesKeepDefaultConsole(string[] arguments) =>
        Assert.False(AcceptanceSdkConsoleRule.RequiresOwnConsole(arguments));
}
