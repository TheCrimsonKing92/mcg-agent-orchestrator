using ArchUnitNET.Domain.Dependencies;
using ArchUnitNET.Loader;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using System.Reflection;

// Parallel-safe: immutable IL/reflection inputs only.
public sealed class ConductorStartSharedLaunchTypeTests
{
    [Fact]
    public void StartAndSelfRelaunch_CallTheSameHandleListLauncher()
    {
        var assembly = typeof(CliConductorCommand).Assembly;
        var architecture = new ArchLoader().LoadAssemblies([assembly]).Build();
        var cli = Assert.Single(architecture.Types, type => type.FullName == typeof(CliConductorCommand).FullName);
        Assert.Contains(cli.Dependencies.OfType<MethodCallDependency>(), call =>
            call.TargetMember.DeclaringType.FullName == typeof(HandleListConductorProcessLauncher).FullName);
        foreach (var caller in new[] { typeof(ConductorLoopHandoff), typeof(HandleListConductorProcessLauncher) })
        {
            // Include compiler-generated local functions, where self-relaunch invokes the native call.
            var calls = architecture.Types.Where(type => type.FullName == caller.FullName ||
                    type.FullName.StartsWith(caller.FullName + "+", StringComparison.Ordinal) ||
                    type.FullName.StartsWith(caller.FullName + "/", StringComparison.Ordinal))
                .SelectMany(type => type.Dependencies.OfType<MethodCallDependency>()).ToArray();
            Assert.Contains(calls, call => call.TargetMember.DeclaringType.FullName ==
                typeof(WindowsHandleListProcessLauncher).FullName &&
                call.TargetMember.Name.StartsWith("Launch(", StringComparison.Ordinal));
        }
    }

    [Theory]
    [InlineData("CreateProcessW")]
    [InlineData("UpdateProcThreadAttribute")]
    public void NativeLaunchInterop_IsDeclaredOnlyBySharedType(string name)
    {
        var declarations = typeof(CliConductorCommand).Assembly.GetTypes()
            .SelectMany(type => type.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
            .Where(method => method.Name == name && (method.Attributes & MethodAttributes.PinvokeImpl) != 0).ToArray();
        Assert.Equal(typeof(WindowsHandleListProcessLauncher), Assert.Single(declarations).DeclaringType);
    }
}
