public sealed class AgentSkillDirectoryInventoryTests
{
    [Xunit.Fact]
    public void SkillDirectoriesAfterRetirementContainExactlyTheEightSelectableSkills()
    {
        string[] expected =
        [
            "criterion-ownership-planning",
            "dotnet-windows-build-hygiene",
            "orchestrator-dogfood",
            "orchestrator-worker-verification",
            "research-evidence",
            "skill-authoring",
            "systematic-debugging",
            "verification-before-completion"
        ];
        expected = expected.OrderBy(name => name, StringComparer.Ordinal).ToArray();

        var root = VerifiedRepositoryRoot.Find();
        var actual = Directory.EnumerateDirectories(
                Path.Combine(root, ".agents", "skills"), "*", SearchOption.TopDirectoryOnly)
            .Select(path => Path.GetFileName(path)!)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        var unexpected = actual.Except(expected, StringComparer.Ordinal);
        var missing = expected.Except(actual, StringComparer.Ordinal);

        Xunit.Assert.True(expected.SequenceEqual(actual, StringComparer.Ordinal),
            $"Worker skill directory inventory differs. Unexpected: [{string.Join(", ", unexpected)}]; missing: [{string.Join(", ", missing)}].");
    }
}
