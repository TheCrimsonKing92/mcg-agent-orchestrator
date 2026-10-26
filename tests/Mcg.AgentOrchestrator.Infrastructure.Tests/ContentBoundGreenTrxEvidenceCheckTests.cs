using System.Security.Cryptography;
using System.Xml.Linq;
using Mcg.AgentOrchestrator.Core;
using static PreTesterGreenSubclassCoverageTests;

// Parallel-safe: all reads and mutations are scoped to a fixture-owned temporary directory.
public sealed class ContentBoundGreenTrxEvidenceCheckTests
{
    [Theory]
    [InlineData("trx-paths-invalid")]
    [InlineData("trx-artifacts-missing")]
    [InlineData("executed-count-missing")]
    [InlineData("no-required-classes")]
    [InlineData("trx-path-set-mismatch")]
    [InlineData("trx-content-not-green")]
    [InlineData("trx-duplicate-test-id")]
    [InlineData("executed-count-mismatch")]
    [InlineData("required-class-missing")]
    [InlineData("passing")]
    [InlineData("subclass-only")]
    public void FirstFailureNamesTheFailedCheckAndMatchesTheBoolean(string scenario)
    {
        using var fixture = new TrxFixture("A");
        var path = fixture.Paths[scenario == "subclass-only" ? 2 : 0];
        if (scenario == "trx-duplicate-test-id")
        {
            var document = XDocument.Load(path);
            XNamespace trx = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";
            var definitions = document.Root!.Element(trx + "TestDefinitions")!;
            definitions.Add(new XElement(definitions.Elements().First()));
            document.Save(path);
        }
        var bytes = File.ReadAllBytes(path);
        IReadOnlyList<string> paths = [path];
        IReadOnlyList<AcceptanceCohortEvidenceArtifact> artifacts =
            [new("trx", path, Convert.ToHexStringLower(SHA256.HashData(bytes)), bytes.Length)];
        var expectedCount = 2;
        IReadOnlyCollection<string> required = ["ConductorDriverTests"];
        switch (scenario)
        {
            case "trx-paths-invalid": paths = []; break;
            case "trx-artifacts-missing": artifacts = []; break;
            case "executed-count-missing": expectedCount = 0; break;
            case "no-required-classes": required = []; break;
            case "trx-path-set-mismatch": artifacts = [artifacts[0] with { Path = fixture.Paths[1] }]; break;
            case "trx-content-not-green": artifacts = [artifacts[0] with { Sha256 = new string('0', 64) }]; break;
            case "executed-count-mismatch": expectedCount = 5; break;
            case "required-class-missing": required = ["UnrelatedTests"]; break;
            case "subclass-only": required = ["ConductorBatchLoopTests"]; break;
        }

        foreach (var allowPrefix in new[] { false, true })
        {
            var failure = ContentBoundGreenTrxEvidenceCheck.FirstFailure(paths, artifacts,
                expectedCount, required, allowPrefix);
            var expectedCheck = scenario switch
            {
                "passing" => null,
                "subclass-only" => allowPrefix ? null : "required-class-missing",
                _ => scenario
            };

            Assert.Equal(expectedCheck, failure?.Check);
            Assert.Equal(failure is null, AcceptanceCohortGateEvidence.HasContentBoundGreenTrxEvidence(
                paths, artifacts, expectedCount, required, allowPrefix));
            if (expectedCheck == "required-class-missing") Assert.Equal(required, failure!.MissingClasses);
            else if (failure is not null) Assert.Empty(failure.MissingClasses);
        }
    }
}
