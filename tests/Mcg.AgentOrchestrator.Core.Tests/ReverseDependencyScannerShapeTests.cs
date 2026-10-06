using Mcg.AgentOrchestrator.Core;

public sealed class ReverseDependencyScannerShapeTests
{
    [Theory]
    [InlineData("internal readonly record struct Probe(int A) { public int B => A; }")]
    [InlineData("internal sealed record class Probe(int A) { public int B => A; }")]
    [InlineData("internal readonly record struct Probe { public int B => 1; }")]
    [InlineData("internal sealed record class Probe { public int B => 1; }")]
    public void RecordWithBodyResolvesItsConsumer(string declaration)
    {
        using var repository = new Repository();
        const string path = "src/Fixture/Probe.cs";
        repository.Write(path, declaration);
        repository.AddConsumer("ProbeConsumerTests", "Probe");

        var result = ReverseDependencyTestImpactReaderLookup.Find(repository.Root, [path]);

        Assert.True(result.Resolved, result.Reason);
        Assert.Null(result.DegradationKind);
        Assert.Equal(["ProbeConsumerTests"], result.TestClassNames);
    }

    [Theory]
    [InlineData("src/Fixture/ProbeFailure.cs", false)]
    [InlineData("src/Fixture/ProbeRow.cs", false)]
    [InlineData("src/Fixture/ProbeFailure.cs", true)]
    [InlineData("src/Fixture/ProbeRow.cs", true)]
    public void BodylessConstructorTypesResolveDirectAndParameterConsumers(string changedPath, bool generic)
    {
        using var repository = new Repository();
        repository.Write("src/Fixture/ProbeFailure.cs",
            "internal sealed class ProbeFailure(string message) : Exception(message);");
        repository.Write("src/Fixture/ProbeRow.cs", generic
            ? "internal sealed record ProbeRow<T>(ProbeFailure Failure);"
            : "internal sealed record ProbeRow(ProbeFailure Failure);");
        repository.AddConsumer("ProbeRowTests", generic ? "ProbeRow<string>" : "ProbeRow");

        var result = ReverseDependencyTestImpactReaderLookup.Find(repository.Root, [changedPath]);

        Assert.True(result.Resolved, result.Reason);
        Assert.Null(result.DegradationKind);
        Assert.Equal(["ProbeRowTests"], result.TestClassNames);
    }

    [Fact]
    public void BodylessConstructorBaseListReachesOwnedConsumer()
    {
        using var repository = new Repository();
        const string path = "src/Fixture/FailureBase.cs";
        repository.Write(path, "internal class FailureBase { }");
        repository.Write("src/Fixture/ProbeFailure.cs",
            "internal sealed class ProbeFailure(string message) : FailureBase;");
        repository.AddConsumer("ProbeFailureTests", "ProbeFailure");

        var result = ReverseDependencyTestImpactReaderLookup.Find(repository.Root, [path]);

        Assert.True(result.Resolved, result.Reason);
        Assert.Null(result.DegradationKind);
        Assert.Equal(["ProbeFailureTests"], result.TestClassNames);
    }

    [Theory]
    [InlineData("struct")]
    [InlineData("class")]
    public void PartialRecordWithBodyKeepsItsPartialFlag(string kind)
    {
        using var repository = new Repository();
        const string path = "src/Fixture/Probe.cs";
        repository.Write(path, $"internal partial record {kind} Probe {{ }}");
        repository.Write("src/Fixture/ProbeExtra.cs", $"internal partial record {kind} Probe {{ }}");
        repository.AddConsumer("ProbeConsumerTests", "Probe");

        var result = ReverseDependencyTestImpactReaderLookup.Find(repository.Root, [path]);

        Assert.True(result.Resolved, result.Reason);
        Assert.Null(result.DegradationKind);
        Assert.Equal(["ProbeConsumerTests"], result.TestClassNames);
    }

    [Fact]
    public void DistinctNonPartialDeclarationsRemainAmbiguous()
    {
        using var repository = new Repository();
        const string path = "src/Fixture/ShadowDuplicate.cs";
        repository.Write(path, "public class ShadowDuplicate { }");
        repository.Write("src/Fixture/OtherDuplicate.cs", "public class ShadowDuplicate { }");
        repository.AddConsumer("ShadowDuplicateTests", "ShadowDuplicate");

        var result = ReverseDependencyTestImpactReaderLookup.Find(repository.Root, [path]);

        Assert.False(result.Resolved);
        Assert.Equal("AmbiguousDeclaration", result.DegradationKind);
        Assert.Empty(result.TestClassNames);
    }

    [Theory]
    [InlineData("namespace Fixture;")]
    [InlineData("internal sealed class Marker;")]
    [InlineData("internal interface IMarker;")]
    [InlineData("internal sealed record Marker;")]
    [InlineData("internal sealed record Marker<T>;")]
    public void SourceWithoutRecognizedDeclarationRemainsUnreadable(string source)
    {
        using var repository = new Repository();
        const string path = "src/Fixture/Marker.cs";
        repository.Write(path, source);
        repository.AddConsumer("MarkerTests", "Marker");

        var result = ReverseDependencyTestImpactReaderLookup.Find(repository.Root, [path]);

        Assert.False(result.Resolved);
        Assert.Equal("Unreadable", result.DegradationKind);
        Assert.Empty(result.TestClassNames);
    }

    private sealed class Repository : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), $"mcg-scanner-shape-{Guid.NewGuid():N}");

        internal Repository()
        {
            Write("src/Fixture/Fixture.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
            Write("tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj",
                "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup><ProjectReference Include=\"../../src/Fixture/Fixture.csproj\" /></ItemGroup></Project>");
        }

        internal void AddConsumer(string className, string typeName) =>
            Write($"tests/Mcg.AgentOrchestrator.Infrastructure.Tests/{className}.cs",
                $"public class {className} {{ private {typeName} value; [Xunit.Fact] public void Example() {{ }} }}");

        internal void Write(string path, string text)
        {
            var absolute = Path.Combine(Root, path);
            Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
            File.WriteAllText(absolute, text);
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
