using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: every test reads and locks only files in its own temporary root.
public sealed class CandidateChangedTypeReferenceReaderTests : IDisposable
{
    private readonly string _root = ConductorDriverTests.CreateTempDirectory();

    [Fact]
    public void DeclarationsIncludePartialRecordsInterfacesAndNestedTypes()
    {
        Write("src/Declarations.cs", """
            namespace Sample;
            internal partial class CliCommandHandlers { internal class Nested { } }
            public record Positional(int Value);
            public record Braced { }
            public record struct ValueRecord(int Value);
            public interface IHandler { }
            public struct ValueType { }
            public enum Mode { One }
            """);
        Write("src/Partial.cs", "partial class CliCommandHandlers { }");

        var types = CandidateChangedTypeReferenceReader.ReadDeclaredTypeNames(
            _root, ["src/Declarations.cs", "src/Partial.cs"]);

        Assert.Equal(new[] { "Braced", "CliCommandHandlers", "IHandler", "Mode", "Nested", "Positional", "ValueRecord", "ValueType" },
            types.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void NoDeclarationsLeavesEmptySetDespiteCommentsAndStrings()
    {
        Write("src/Empty.cs", "// class CommentType {}\nSystem.Console.WriteLine(\"record StringType;\");");

        Assert.Empty(CandidateChangedTypeReferenceReader.ReadDeclaredTypeNames(_root, ["src/Empty.cs"]));
    }

    [Theory]
    [InlineData("class CliCommandHandlersTests { }", false)]
    [InlineData("class PrefixCliCommandHandlers { }", false)]
    [InlineData("// CliCommandHandlers\nclass Other { string Name = \"CliCommandHandlers\"; }", false)]
    [InlineData("class Other { CliCommandHandlers? Subject; }", true)]
    [InlineData("class Other { @CliCommandHandlers? Subject; }", true)]
    public void ReferencesMatchWholeIdentifierTokens(string source, bool expectedMatch)
    {
        Write("src/Changed.cs", "partial class CliCommandHandlers { }");
        Write("tests/References.cs", source);
        var types = CandidateChangedTypeReferenceReader.ReadDeclaredTypeNames(_root, ["src/Changed.cs"]);

        var matches = CandidateChangedTypeReferenceReader.MatchReferencedTypes(_root, ["tests/References.cs"], types);

        Assert.Equal(expectedMatch ? new[] { "CliCommandHandlers" } : Array.Empty<string>(), matches);
    }

    [Fact]
    public void ReferencesAcrossFilesAreDistinctAndSorted()
    {
        Write("src/Changed.cs", "class Zebra { } class Alpha { }");
        Write("tests/First.cs", "class First { Zebra z; Alpha a; }");
        Write("tests/Second.cs", "class Second { Alpha a; }");
        var types = CandidateChangedTypeReferenceReader.ReadDeclaredTypeNames(_root, ["src/Changed.cs"]);

        Assert.Equal(new[] { "Alpha", "Zebra" }, CandidateChangedTypeReferenceReader.MatchReferencedTypes(
            _root, ["tests/First.cs", "tests/Second.cs"], types));
    }

    [Theory]
    [InlineData("tests/Outside.cs")]
    [InlineData("src/Notes.txt")]
    [InlineData("src/../tests/Outside.cs")]
    [InlineData("src/../../Outside.cs")]
    [InlineData("src/Missing.cs")]
    public void PathsOutsideChangedCSharpSourceContributeNothing(string changedPath)
    {
        Write("tests/Outside.cs", "class Outside { }");
        Write("src/Notes.txt", "class Notes { }");

        Assert.Empty(CandidateChangedTypeReferenceReader.ReadDeclaredTypeNames(_root, [changedPath]));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AbsentRootReturnsEmptyEvidence(bool nullRoot)
    {
        var root = nullRoot ? null : Path.Combine(_root, "missing-root");
        Assert.Empty(CandidateChangedTypeReferenceReader.ReadDeclaredTypeNames(root, ["src/Changed.cs"]));
        Assert.Empty(CandidateChangedTypeReferenceReader.MatchReferencedTypes(
            root, ["tests/References.cs"], new HashSet<string>(StringComparer.Ordinal) { "CliCommandHandlers" }));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LockedSourceDiscardsPartialEvidenceWithoutThrowing(bool lockTestSource)
    {
        Write("src/First.cs", "class CliCommandHandlers { }");
        Write("src/Locked.cs", "class Other { }");
        Write("tests/First.cs", "class First { CliCommandHandlers? subject; }");
        Write("tests/Locked.cs", "class Second { CliCommandHandlers? subject; }");
        var path = lockTestSource ? "tests/Locked.cs" : "src/Locked.cs";
        using var fileLock = new FileStream(Path.Combine(_root, path), FileMode.Open, FileAccess.Read, FileShare.None);

        if (lockTestSource)
        {
            var types = CandidateChangedTypeReferenceReader.ReadDeclaredTypeNames(_root, ["src/First.cs"]);
            Assert.Equal(new[] { "CliCommandHandlers" }, types);
            Assert.Empty(CandidateChangedTypeReferenceReader.MatchReferencedTypes(
                _root, ["tests/First.cs", "tests/Locked.cs"], types));
        }
        else
        {
            Assert.Empty(CandidateChangedTypeReferenceReader.ReadDeclaredTypeNames(
                _root, ["src/First.cs", "src/Locked.cs"]));
        }
    }

    private void Write(string path, string source)
    {
        var fullPath = Path.Combine(_root, path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, source);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
