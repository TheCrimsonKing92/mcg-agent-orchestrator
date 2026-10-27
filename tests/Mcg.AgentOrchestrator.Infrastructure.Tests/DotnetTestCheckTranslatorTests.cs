using System.Reflection;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using AcceptanceManifestCheck = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.AcceptanceManifestCheck;

// Parallel-safe: each test owns its temporary tree; no processes or global state are used.
public sealed class DotnetTestCheckTranslatorTests
{
    private const string Project = "tests/Sample.Tests/Sample.Tests.csproj";
    private const string Filter = "FullyQualifiedName~Sample.Tests.WidgetFacts";
    private const string CommandLine =
        "dotnet test --project tests/Sample.Tests/Sample.Tests.csproj --filter FullyQualifiedName~Sample.Tests.WidgetFacts";

    public static TheoryData<string[], string?, string[]> CommandCases => new()
    {
        { ["dotnet", "test", "--project", Project, "--filter", Filter], Project, ["--filter", Filter] },
        { ["dotnet", "test", Project, "--no-build"], Project, ["--no-build"] },
        { ["dotnet", "test", "--filter", "X"], null, ["--filter", "X"] },
        { ["dotnet", "test"], null, [] },
        { ["dotnet", "test", "--project"], null, ["--project"] }
    };

    [Theory]
    [MemberData(nameof(CommandCases))]
    public void FromCommand_ProjectForms_PreserveFields(string[] command, string? project, string[] arguments)
    {
        var root = SharedTestSupport.CreateTempDirectory();
        try
        {
            Assert.Empty(Directory.EnumerateFiles(root, "*.csproj", SearchOption.AllDirectories));

            var check = DotnetTestCheckTranslator.FromCommand(root, "n", command);

            Assert.Equal("dotnet-test", check.Type);
            Assert.Equal("n", check.Name);
            Assert.Equal("mtp", check.Runner);
            Assert.Equal(project, check.Project);
            Assert.Equal(arguments, check.Arguments.ToArray());
        }
        finally
        {
            SharedTestSupport.RemoveTempDirectory(root);
        }
    }

    [Fact]
    public void ResolveRunner_UnconditionalFalse_MatchesVerifierVstest()
    {
        var root = SharedTestSupport.CreateTempDirectory();
        try
        {
            WriteProject(root);

            Assert.Equal("vstest", DotnetTestCheckTranslator.ResolveRunner(root, Project));
            Assert.Equal("vstest", GoalAcceptanceVerifier.ResolveDotnetTestRunner(root, Project));
        }
        finally
        {
            SharedTestSupport.RemoveTempDirectory(root);
        }
    }

    [Fact]
    public void ResolveRunner_LoaderIOException_MatchesVerifierMtp()
    {
        var root = SharedTestSupport.CreateTempDirectory();
        try
        {
            WriteProject(root);
            var calls = 0;
            System.Xml.Linq.XDocument LoadProject(string path)
            {
                Assert.Equal(Path.GetFullPath(Path.Combine(root, Project)), path);
                calls++;
                throw new IOException("Project cannot be read.");
            }

            Assert.Equal("mtp", DotnetTestCheckTranslator.ResolveRunner(root, Project, LoadProject));
            Assert.Equal("mtp", GoalAcceptanceVerifier.ResolveDotnetTestRunner(root, Project, LoadProject));
            Assert.Equal(2, calls);
        }
        finally
        {
            SharedTestSupport.RemoveTempDirectory(root);
        }
    }

    [Fact]
    public void TryFromCommandLine_RequiredPolicy_MatchesAllManifestFields()
    {
        var root = SharedTestSupport.CreateTempDirectory();
        try
        {
            WriteProject(root);
            var policy = new VerificationPolicy("focused", true, false,
                [new VerificationPolicyCheck("focused", "dotnet-test", true, CommandLine, "focused tests")]);

            var actual = Assert.Single(GoalAcceptanceVerifier.MapRequiredPolicyChecksForTests(root, policy));
            var expected = Assert.IsType<AcceptanceManifestCheck>(
                DotnetTestCheckTranslator.TryFromCommandLine(root, "focused", CommandLine));

            Assert.Equal("vstest", expected.Runner);
            Assert.Equal(expected.Name, actual.Name);
            Assert.Equal(expected.Type, actual.Type);
            Assert.Equal(expected.Command, actual.Command);
            Assert.Equal(expected.Project, actual.Project);
            Assert.Equal(expected.Arguments.ToArray(), actual.Arguments.ToArray());
            Assert.Equal(expected.Runner, actual.Runner);
            Assert.Equal(expected.TimeoutMinutes, actual.TimeoutMinutes);
            Assert.Equal(expected.Advisory, actual.Advisory);
        }
        finally
        {
            SharedTestSupport.RemoveTempDirectory(root);
        }
    }

    [Theory]
    [InlineData("PolicyCheckToManifestCheck")]
    [InlineData("DotnetCommandToManifestCheck")]
    [InlineData("SplitCommandLine")]
    public void Verifier_MovedMembers_DeclaresNoFormerOwner(string name)
    {
        const BindingFlags declaredMembers = BindingFlags.Public | BindingFlags.NonPublic |
            BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly;

        Assert.Empty(typeof(GoalAcceptanceVerifier).GetMember(name, declaredMembers));
    }

    private static void WriteProject(string root)
    {
        var path = Path.Combine(root, Project);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path,
            "<Project><PropertyGroup><UseMicrosoftTestingPlatformRunner>false</UseMicrosoftTestingPlatformRunner></PropertyGroup></Project>");
    }
}
