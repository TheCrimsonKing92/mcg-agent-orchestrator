using Mcg.AgentOrchestrator.Infrastructure;

// Pure syntax cases and uniquely owned temporary directories are parallel-safe.
public sealed class DeclaredStaticSkipScannerTests
{
    [Xunit.Theory]
    [Xunit.InlineData("[Xunit.Fact(Skip = \"text\")]", true)]
    [Xunit.InlineData("[Xunit.Theory(Skip = \"text\")]", true)]
    [Xunit.InlineData("[Xunit.Fact(Skip = \"text\", SkipUnless = \"IsSet\")]", false)]
    [Xunit.InlineData("[Xunit.Fact(SkipWhen = \"IsSet\", Skip = \"text\")]", false)]
    [Xunit.InlineData("[Xunit.Fact]", false)]
    [Xunit.InlineData("[Xunit.Fact(Skip = Reason)]", false)]
    [Xunit.InlineData("[Xunit.Fact(Skip = nameof(Reason))]", false)]
    [Xunit.InlineData("[Xunit.Fact(Skip = \"one\" + \"two\")]", false)]
    [Xunit.InlineData("[Xunit.Theory][Xunit.InlineData(1, Skip = \"text\")]", false)]
    [Xunit.InlineData("[CustomFact(Skip = \"text\")]", false)]
    public void MethodAttribute_OnlyUnconditionalLiteralSkipIsDeclared(string attribute, bool expected)
    {
        var source = $$"""
            public class Lane0Tests
            {
                const string Reason = "text";
                {{attribute}} public void Skipped() { }
            }
            """;

        var declared = DeclaredStaticSkipScanner.DeclaredIn(source);

        if (expected) Assert.Equal("Lane0Tests.Skipped", Assert.Single(declared));
        else Assert.Empty(declared);
    }

    [Xunit.Fact]
    public void ClassAttribute_DoesNotDeclareMethodSkip()
    {
        var declared = DeclaredStaticSkipScanner.DeclaredIn("""
            [Xunit.Fact(Skip = "text")]
            public class Lane0Tests { [Xunit.Fact] public void Executes() { } }
            """);

        Assert.Empty(declared);
    }

    [Xunit.Fact]
    public void NamespacedNestedClass_PreservesTrxTypeIdentity()
    {
        var declared = DeclaredStaticSkipScanner.DeclaredIn("""
            namespace Tests;
            public class Outer
            {
                public class Inner { [Xunit.Fact(Skip = "text")] public void Skipped() { } }
            }
            """);

        Assert.Equal("Tests.Outer+Inner.Skipped", Assert.Single(declared));
    }

    [Xunit.Fact]
    public void ProjectScan_ReadsNestedSourcesAndExcludesBuildOutputs()
    {
        var root = Path.Combine(Path.GetTempPath(), nameof(DeclaredStaticSkipScannerTests), Guid.NewGuid().ToString("N"));
        try
        {
            foreach (var folder in new[] { "nested", "bin", "obj" })
            {
                Directory.CreateDirectory(Path.Combine(root, folder));
                var source = $"public class {folder}Tests {{ [Xunit.Fact(Skip = \"text\")] public void Skipped() {{ }} }}";
                File.WriteAllText(Path.Combine(root, folder, "Fixture.cs"), source);
            }

            Assert.Equal("nestedTests.Skipped", Assert.Single(DeclaredStaticSkipScanner.Scan(root)));
            Assert.Empty(DeclaredStaticSkipScanner.Scan(Path.Combine(root, "missing")));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
