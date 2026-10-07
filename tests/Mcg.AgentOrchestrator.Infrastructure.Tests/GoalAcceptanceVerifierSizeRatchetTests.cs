using Mcg.AgentOrchestrator.Infrastructure;

public sealed class GoalAcceptanceVerifierSizeRatchetTests
{
    [Fact]
    public void RealTable_EveryEntryWithinCeiling()
    {
        var repositoryRoot = FindRepositoryRoot();
        var paths = SourceSizeRatchet.SeededCeilings.Select(entry => entry.RelativePath).ToArray();

        Assert.NotEmpty(SourceSizeRatchet.SeededCeilings);
        Assert.Equal(paths.Length, paths.Distinct(StringComparer.Ordinal).Count());
        AssertNoViolations(SourceSizeRatchet.Evaluate(repositoryRoot, SourceSizeRatchet.SeededCeilings));
    }

    [Fact]
    public void ParsedAuthority_MatchesCompiledSeededCeilings()
    {
        var parsed = SourceSizeRatchetPreflight.TryReadAuthority(FindRepositoryRoot());

        Assert.NotNull(parsed);
        Assert.Equal(SourceSizeRatchet.SeededCeilings, parsed);
    }

    [Fact]
    public void BackstopAssertion_ViolatingTable_GoesRed()
    {
        var root = CreateTempDirectory();
        try
        {
            WriteLines(root, "backstop.cs", 3, "line");
            var violations = SourceSizeRatchet.Evaluate(
                root,
                [new SourceSizeCeiling("backstop.cs", 2)]);

            var exception = Assert.Throws<Xunit.Sdk.TrueException>(() => AssertNoViolations(violations));
            Assert.Contains("backstop.cs has 3 lines", exception.Message, StringComparison.Ordinal);
            Assert.Contains("recorded ceiling of 2", exception.Message, StringComparison.Ordinal);
            Assert.Contains("Extract behavior to a collaborator", exception.Message, StringComparison.Ordinal);
            Assert.Contains("raise the recorded ceiling", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void RealDocumentation_PointsAtAuthorityAndRecordsNoCeilings()
    {
        var repositoryRoot = FindRepositoryRoot();

        AssertNoDocumentationViolations(SourceSizeRatchet.EvaluateDocumentation(
            ReadDocumentationLines(repositoryRoot),
            SourceSizeRatchet.SeededCeilings));
    }

    [Fact]
    public void AuthoritativeCeilingChange_TakesEffectWithoutASecondEdit()
    {
        var repositoryRoot = FindRepositoryRoot();
        var authoritativeEntry = SourceSizeRatchet.SeededCeilings[0];
        var changedAuthority = new[] { authoritativeEntry with { MaximumLineCount = 1 } };
        var actualLineCount = File.ReadLines(Path.Combine(
            repositoryRoot,
            authoritativeEntry.RelativePath.Replace('/', Path.DirectorySeparatorChar))).Count();

        var violation = Assert.Single(SourceSizeRatchet.Evaluate(repositoryRoot, changedAuthority));

        Assert.Equal(authoritativeEntry.RelativePath, violation.RelativePath);
        Assert.Equal(actualLineCount, violation.ActualLineCount);
        Assert.Equal(1, violation.MaximumLineCount);
        Assert.Contains(authoritativeEntry.RelativePath, violation.Message, StringComparison.Ordinal);
        Assert.Contains(
            actualLineCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
            violation.Message,
            StringComparison.Ordinal);
        Assert.Contains("recorded ceiling of 1", violation.Message, StringComparison.Ordinal);
        AssertNoDocumentationViolations(SourceSizeRatchet.EvaluateDocumentation(
            ReadDocumentationLines(repositoryRoot),
            changedAuthority));
    }

    [Fact]
    public void DocumentationVerdict_IsIndependentOfCeilingValues()
    {
        var repositoryRoot = FindRepositoryRoot();
        var documentationLines = ReadDocumentationLines(repositoryRoot);
        var raisedCeilings = SourceSizeRatchet.SeededCeilings
            .Select(ceiling => ceiling with { MaximumLineCount = ceiling.MaximumLineCount + 1 })
            .ToArray();
        var minimumCeilings = SourceSizeRatchet.SeededCeilings
            .Select(ceiling => ceiling with { MaximumLineCount = 1 })
            .ToArray();

        AssertNoDocumentationViolations(SourceSizeRatchet.EvaluateDocumentation(
            documentationLines,
            SourceSizeRatchet.SeededCeilings));
        AssertNoDocumentationViolations(SourceSizeRatchet.EvaluateDocumentation(
            documentationLines,
            raisedCeilings));
        AssertNoDocumentationViolations(SourceSizeRatchet.EvaluateDocumentation(
            documentationLines,
            minimumCeilings));
    }

    [Fact]
    public void Documentation_ReintroducedCeilingRecord_ProducesLoudViolation()
    {
        var ceiling = SourceSizeRatchet.SeededCeilings[0];
        var authorityPointer =
            $"See {SourceSizeRatchet.SeededCeilingsSymbol} and {SourceSizeRatchet.SeededClassCeilingsSymbol} in {SourceSizeRatchet.SourcePath}.";
        var documents = new[]
        {
            new[]
            {
                SourceSizeRatchet.DocumentationSectionHeading,
                authorityPointer,
                $"| `{ceiling.RelativePath}` | {ceiling.MaximumLineCount} |",
            },
            new[]
            {
                SourceSizeRatchet.DocumentationSectionHeading,
                authorityPointer,
                $"- Guarded file {ceiling.RelativePath.Replace('/', '\\')} has ceiling {ceiling.MaximumLineCount}.",
            },
        };

        foreach (var document in documents)
        {
            var violation = Assert.Single(SourceSizeRatchet.EvaluateDocumentation(document, new[] { ceiling }));

            Assert.Equal("duplicated-ceiling-record", violation.Rule);
            Assert.Equal(3, violation.LineNumber);
            Assert.Contains(ceiling.RelativePath, violation.Message, StringComparison.Ordinal);
            Assert.Contains("line 3", violation.Message, StringComparison.Ordinal);
            Assert.Contains(SourceSizeRatchet.SeededCeilingsSymbol, violation.Message, StringComparison.Ordinal);
            Assert.Contains(SourceSizeRatchet.SourcePath, violation.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Documentation_AuthoritySourceRow_AllowsOnlyPointer()
    {
        var authorityCeiling = new SourceSizeCeiling(SourceSizeRatchet.SourcePath, 1);
        var authorityPointer =
            $"See {SourceSizeRatchet.SeededCeilingsSymbol} and {SourceSizeRatchet.SeededClassCeilingsSymbol} in {SourceSizeRatchet.SourcePath}.";
        var validDocument = new[]
        {
            SourceSizeRatchet.DocumentationSectionHeading,
            authorityPointer,
        };

        AssertNoDocumentationViolations(SourceSizeRatchet.EvaluateDocumentation(
            validDocument,
            new[] { authorityCeiling }));

        var duplicatedDocument = validDocument.Append(
            $"| `{SourceSizeRatchet.SourcePath}` | {authorityCeiling.MaximumLineCount} |");
        var violation = Assert.Single(SourceSizeRatchet.EvaluateDocumentation(
            duplicatedDocument,
            new[] { authorityCeiling }));

        Assert.Equal("duplicated-ceiling-record", violation.Rule);
        Assert.Equal(3, violation.LineNumber);
        Assert.Contains(SourceSizeRatchet.SourcePath, violation.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Documentation_MissingSectionOrPointer_ProducesLoudViolation()
    {
        var missingSection = Assert.Single(SourceSizeRatchet.EvaluateDocumentation(
            new[] { "# Decomposition plan" },
            Array.Empty<SourceSizeCeiling>()));

        Assert.Equal("missing-ratchet-section", missingSection.Rule);
        Assert.Contains(SourceSizeRatchet.DocumentationSectionHeading, missingSection.Message, StringComparison.Ordinal);

        var missingPointers = SourceSizeRatchet.EvaluateDocumentation(
            new[] { SourceSizeRatchet.DocumentationSectionHeading },
            Array.Empty<SourceSizeCeiling>());

        Assert.Equal(3, missingPointers.Count);
        Assert.All(missingPointers, violation => Assert.Equal("missing-authority-pointer", violation.Rule));
        Assert.Contains(
            missingPointers,
            violation => violation.Message.Contains(SourceSizeRatchet.SeededCeilingsSymbol, StringComparison.Ordinal));
        Assert.Contains(
            missingPointers,
            violation => violation.Message.Contains(SourceSizeRatchet.SourcePath, StringComparison.Ordinal));
        Assert.Contains(
            missingPointers,
            violation => violation.Message.Contains(SourceSizeRatchet.SeededClassCeilingsSymbol, StringComparison.Ordinal));
    }

    [Fact]
    public void SyntheticTable_OneCompliantAndOneExceeding_FailsOnlyExceeding()
    {
        var root = CreateTempDirectory();
        try
        {
            WriteLines(root, "compliant.txt", 5, "line");
            WriteLines(root, "exceeding.txt", 12, "line");
            var table = new[]
            {
                new SourceSizeCeiling("compliant.txt", 8),
                new SourceSizeCeiling("exceeding.txt", 4),
            };

            var violations = SourceSizeRatchet.Evaluate(root, table);
            var violation = Assert.Single(violations);
            var exception = Record.Exception(() => AssertNoViolations(violations));

            Assert.Equal("exceeding.txt", violation.RelativePath);
            Assert.Equal(12, violation.ActualLineCount);
            Assert.Equal(4, violation.MaximumLineCount);
            Assert.NotNull(exception);
            Assert.Contains("exceeding.txt", exception.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("compliant.txt", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void SyntheticTable_AtBelowAndZero_ProducesNoViolations()
    {
        var root = CreateTempDirectory();
        try
        {
            WriteLines(root, "at-ceiling.txt", 4, "line");
            WriteLines(root, "below-ceiling.txt", 3, "line");
            WriteLines(root, "empty.txt", 0, "line");
            var table = new[]
            {
                new SourceSizeCeiling("at-ceiling.txt", 4),
                new SourceSizeCeiling("below-ceiling.txt", 4),
                new SourceSizeCeiling("empty.txt", 4),
            };

            Assert.Empty(SourceSizeRatchet.Evaluate(root, table));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void OverCeilingMessage_NamesFileActualCeilingAndBothRemedies()
    {
        var root = CreateTempDirectory();
        try
        {
            WriteLines(root, "exceeding.txt", 12, "line");

            var violation = Assert.Single(SourceSizeRatchet.Evaluate(
                root,
                new[] { new SourceSizeCeiling("exceeding.txt", 4) }));

            Assert.Contains("exceeding.txt", violation.Message, StringComparison.Ordinal);
            Assert.Contains("12", violation.Message, StringComparison.Ordinal);
            Assert.Contains("recorded ceiling of 4", violation.Message, StringComparison.Ordinal);
            Assert.Contains(
                "Extract behavior to a collaborator and lower the ceiling",
                violation.Message,
                StringComparison.Ordinal);
            Assert.Contains(
                "raise the recorded ceiling for this entry deliberately",
                violation.Message,
                StringComparison.Ordinal);
            Assert.Contains("justification in the same change", violation.Message, StringComparison.Ordinal);
            Assert.Contains("SourceSizeRatchet.SeededCeilings", violation.Message, StringComparison.Ordinal);
            Assert.Contains(SourceSizeRatchet.DocumentationPath, violation.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void SyntheticTable_MissingFile_ProducesLoudViolation()
    {
        var root = CreateTempDirectory();
        try
        {
            var violation = Assert.Single(SourceSizeRatchet.Evaluate(
                root,
                new[] { new SourceSizeCeiling("renamed-or-deleted.cs", 10) }));

            Assert.Equal("renamed-or-deleted.cs", violation.RelativePath);
            Assert.Null(violation.ActualLineCount);
            Assert.Contains("does not exist", violation.Message, StringComparison.Ordinal);
            Assert.Contains("rename or delete", violation.Message, StringComparison.Ordinal);
            Assert.Contains("update SourceSizeRatchet.SeededCeilings", violation.Message, StringComparison.Ordinal);
            Assert.Contains("in the same change", violation.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void SyntheticTable_UnreadableFile_ProducesLoudViolation()
    {
        var root = CreateTempDirectory();
        try
        {
            WriteLines(root, "unreadable.cs", 1, "line");

            var violation = Assert.Single(SourceSizeRatchet.Evaluate(
                root,
                new[] { new SourceSizeCeiling("unreadable.cs", 10) },
                _ => throw new IOException("simulated read failure")));

            Assert.Equal("unreadable.cs", violation.RelativePath);
            Assert.Null(violation.ActualLineCount);
            Assert.Contains("could not be read", violation.Message, StringComparison.Ordinal);
            Assert.Contains("IOException: simulated read failure", violation.Message, StringComparison.Ordinal);
            Assert.Contains(SourceSizeRatchet.DocumentationPath, violation.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void EqualSizeDifferentContent_ProducesSameVerdict()
    {
        var root = CreateTempDirectory();
        try
        {
            var atCeilingA = WriteLines(root, "at-ceiling-a.txt", 4, "alpha");
            var atCeilingB = WriteLines(root, "at-ceiling-b.txt", 4, "unrelated edit");
            var overCeilingA = WriteLines(root, "over-ceiling-a.txt", 5, "alpha");
            var overCeilingB = WriteLines(root, "over-ceiling-b.txt", 5, "unrelated edit");

            Assert.NotEqual(File.ReadAllText(atCeilingA), File.ReadAllText(atCeilingB));
            var table = new[]
            {
                new SourceSizeCeiling(Path.GetFileName(atCeilingA), 4),
                new SourceSizeCeiling(Path.GetFileName(atCeilingB), 4),
                new SourceSizeCeiling(Path.GetFileName(overCeilingA), 4),
                new SourceSizeCeiling(Path.GetFileName(overCeilingB), 4),
            };

            var violations = SourceSizeRatchet.Evaluate(root, table);

            Assert.Equal(
                new[] { "over-ceiling-a.txt", "over-ceiling-b.txt" },
                violations.Select(violation => violation.RelativePath));
            Assert.All(violations, violation => Assert.Equal(5, violation.ActualLineCount));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string WriteLines(string root, string fileName, int lineCount, string content)
    {
        var path = Path.Combine(root, fileName);
        File.WriteAllLines(path, Enumerable.Repeat(content, lineCount));
        return path;
    }

    private static string[] ReadDocumentationLines(string repositoryRoot)
    {
        var documentationPath = Path.Combine(
            repositoryRoot,
            SourceSizeRatchet.DocumentationPath.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(
            File.Exists(documentationPath),
            $"Size-ratchet guidance document '{SourceSizeRatchet.DocumentationPath}' does not exist.");
        return File.ReadAllLines(documentationPath);
    }

    private static void AssertNoViolations(IReadOnlyList<SourceSizeViolation> violations)
    {
        Assert.True(
            violations.Count == 0,
            string.Join(Environment.NewLine, violations.Select(violation => violation.Message)));
    }

    private static void AssertNoDocumentationViolations(
        IReadOnlyList<SourceSizeDocumentationViolation> violations)
    {
        Assert.True(
            violations.Count == 0,
            string.Join(Environment.NewLine, violations.Select(violation => violation.Message)));
    }
}
