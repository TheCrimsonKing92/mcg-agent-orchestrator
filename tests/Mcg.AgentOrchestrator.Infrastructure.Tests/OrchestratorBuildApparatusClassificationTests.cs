using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class OrchestratorBuildApparatusClassificationTests : WorkerDispatchTestSupport
{
    private const string Incident =
        "FAIL build: 1 error(s) (Invoke-WorkerBuildCheck) projects=1 warnings=0 captured_chars=0 elapsed_ms=300412 logs=unavailable\n" +
        @"error: build-check apparatus failure: Timed out waiting for build lease execution lock: C:\x\build-slots\build-1.lock";
    private const string CompilerError = "src/Feature.cs(12,4): error CS0103: missing [App.csproj]";

    [Xunit.Fact]
    public void ClassifyOutput_LockTimeout_HasNoCompilerVerdict()
    {
        var result = OrchestratorBuildEvidenceCheck.ClassifyOutput(1, Incident);

        Assert.False(result.Ran);
        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Timed out waiting for build lease execution lock", result.Output,
            StringComparison.Ordinal);
    }

    [Xunit.Theory]
    [Xunit.InlineData(1, Incident + "\n" + CompilerError, true)]
    [Xunit.InlineData(1, "FAIL build: 1 error(s)", true)]
    [Xunit.InlineData(1, "FAIL build: missing project(s)", false)]
    [Xunit.InlineData(0, "PASS build: 0 errors", true)]
    public void ClassifyOutput_ExistingVerdicts_PreserveRan(int exitCode, string text, bool ran)
    {
        var result = OrchestratorBuildEvidenceCheck.ClassifyOutput(exitCode, text);

        Assert.Equal(ran, result.Ran);
        Assert.Equal(exitCode, result.ExitCode);
    }

    [Xunit.Theory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public void Resolve_TesterTimeout_RequiresPositiveCompilerEvidence(bool compilerError)
    {
        var root = CreateSeededDispatchRepository();
        var directory = Path.Combine(root, "src", "Feature");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "Feature.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        File.WriteAllText(Path.Combine(directory, "Feature.cs"), "public sealed class Feature { }");
        var builds = 0;

        var result = OrchestratorBuildEvidenceCheck.Resolve(
            root, AgentRole.Tester, ["src/Feature/Feature.cs"], [], false,
            workerBuildReceipt: () => new(false, "receipt-missing"),
            _ =>
            {
                builds++;
                return OrchestratorBuildEvidenceCheck.ClassifyOutput(1,
                    Incident + (compilerError ? "\n" + CompilerError : string.Empty));
            });

        Assert.Equal(1, builds);
        Assert.Equal(!compilerError, result.MissingEvidence);
        Assert.Equal(compilerError, result.BuildFailed);
        var diagnostic = result.AppendDiagnostic(null);
        var missingMarker = DispatchFailureDiagnosticMarker.Format(DispatchFailureDiagnosticMarker.WorkerBuildEvidenceMissing);
        var failedMarker = DispatchFailureDiagnosticMarker.Format(DispatchFailureDiagnosticMarker.WorkerBuildCheckFailed);
        Assert.Contains(compilerError ? failedMarker : missingMarker, diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain(compilerError ? missingMarker : failedMarker, diagnostic, StringComparison.Ordinal);
    }
}
