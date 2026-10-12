using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public class AcceptanceArtifactFileNamesTests
{
    // Parallel-safe: pure naming helpers and read-only repository source; no shared mutations.
    [Theory]
    [InlineData("Infrastructure Tests --filter A|B", "infrastructure-tests-filter-a-b")]
    [InlineData("!@#$%^&*()", "check")]
    [InlineData("", "check")]
    [InlineData(" \t\r\n", "check")]
    [InlineData("---Hello World...", "hello-world")]
    public void Slug_normalizes_names_and_uses_empty_fallback(string value, string expected)
    {
        Assert.Equal(expected, AcceptanceArtifactFileNames.Slug(value));
    }

    [Fact]
    public void SanitizeFileName_replaces_every_invalid_character()
    {
        var invalid = Path.GetInvalidFileNameChars();
        Assert.NotEmpty(invalid);
        var value = "start" + new string(invalid) + "end";

        Assert.Equal("start" + new string('-', invalid.Length) + "end",
            AcceptanceArtifactFileNames.SanitizeFileName(value));
    }

    [Theory]
    [InlineData("-..name..-", "name")]
    [InlineData("...", "acceptance-test-results")]
    public void SanitizeFileName_trims_edges_and_uses_empty_fallback(string value, string expected)
    {
        Assert.Equal(expected, AcceptanceArtifactFileNames.SanitizeFileName(value));
    }

    [Theory]
    [InlineData(20)]
    [InlineData(196)]
    public void BoundFileName_preserves_names_up_to_exactly_200_characters(int stemLength)
    {
        var stem = new string('a', stemLength);

        Assert.Equal(stem + ".trx", AcceptanceArtifactFileNames.BoundFileName(stem, ".trx"));
    }

    [Fact]
    public void BoundFileName_truncates_at_201_characters()
    {
        var stem = new string('a', 197);
        var result = AcceptanceArtifactFileNames.BoundFileName(stem, ".trx");

        Assert.NotEqual(stem + ".trx", result);
        Assert.Equal(200, result.Length);
        Assert.EndsWith(".trx", result);
        Assert.Contains(GoalAcceptanceVerifier.ShortHash(stem), result);
    }

    [Fact]
    public void BoundFileName_bounds_324_character_stem_and_preserves_hash_and_suffix()
    {
        var stem = new string('a', 324);
        var result = AcceptanceArtifactFileNames.BoundFileName(stem, ".trx");

        Assert.True(result.Length <= 200);
        Assert.EndsWith(".trx", result);
        Assert.Contains(GoalAcceptanceVerifier.ShortHash(stem), result);
    }

    [Fact]
    public void BoundFileName_disambiguates_names_sharing_a_long_prefix()
    {
        var prefix = new string('a', 300);
        var first = AcceptanceArtifactFileNames.BoundFileName(prefix + "first", ".trx");
        var second = AcceptanceArtifactFileNames.BoundFileName(prefix + "second", ".trx");

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void BoundFileName_trims_separators_at_the_truncated_stem_edge()
    {
        var stem = new string('a', 177) + "-." + new string('b', 145);

        Assert.Equal(new string('a', 177) + "-" + GoalAcceptanceVerifier.ShortHash(stem) + ".trx",
            AcceptanceArtifactFileNames.BoundFileName(stem, ".trx"));
    }

    [Fact]
    public void BoundFileName_returns_hash_and_suffix_when_no_stem_room_remains()
    {
        var stem = new string('a', 324);
        var suffix = new string('s', 190);

        // The existing no-room branch preserves the suffix even when hash + suffix exceeds 200.
        Assert.Equal(GoalAcceptanceVerifier.ShortHash(stem) + suffix,
            AcceptanceArtifactFileNames.BoundFileName(stem, suffix));
    }

    [Fact]
    public void Verifier_delegates_generated_names_without_retaining_helpers()
    {
        var folder = Path.Combine(VerifiedRepositoryRoot.Find(), "src",
            "Mcg.AgentOrchestrator.Infrastructure", "Workspaces");
        foreach (var name in new[] { "GoalAcceptanceVerifier.cs", "GoalAcceptanceVerifier.CaptureLimit.cs" })
        {
            var path = Path.Combine(folder, name);
            Assert.True(File.Exists(path), $"Missing verifier source: {path}");
            var source = File.ReadAllText(path);
            Assert.True(!source.Contains("Slug(", StringComparison.Ordinal) ||
                source.Contains("AcceptanceArtifactFileNames.Slug(", StringComparison.Ordinal),
                $"Missing generated-name delegation in {name}");
            Assert.DoesNotContain("private static string Slug(", source);
            Assert.DoesNotContain("private static string SanitizeFileName(", source);
            Assert.DoesNotContain("private static string BoundFileName(", source);
            Assert.DoesNotContain("MaxGeneratedFileNameLength", source);
            Assert.DoesNotMatch(@"(?m)^\s*Slug\s*,", source);
        }

        var files = Directory.GetFiles(folder, "GoalAcceptanceVerifier*.cs");
        Assert.NotEmpty(files);
        foreach (var path in files)
        {
            var source = File.ReadAllText(path);
            foreach (Match match in Regex.Matches(source, @"Slug\("))
            {
                var prefix = source[..match.Index];
                Assert.True(prefix.EndsWith("AcceptanceArtifactFileNames.", StringComparison.Ordinal) ||
                    prefix.EndsWith("hooks.Attempt", StringComparison.Ordinal),
                    $"Unqualified Slug call in {path} at offset {match.Index}");
            }
        }
    }

    [Fact]
    public void Artifact_file_names_source_triggers_post_landing_canary()
    {
        var result = PostLandingCanaryTrigger.Evaluate(
            ["src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/AcceptanceArtifactFileNames.cs"]);

        Assert.True(result.ShouldRun);
    }
}
