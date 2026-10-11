using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: every test owns a unique temporary directory.
public sealed class OwnerAnswerFileTests
{
    private static readonly DateTimeOffset Recorded = new(2026, 10, 11, 12, 34, 56, TimeSpan.Zero);

    [Fact]
    public void Write_UnsortedAnswers_RoundTripsAllFieldsInOrdinalOrder()
    {
        using var fixture = new ProjectOnboardingFixture("no-solution");
        OwnerAnswer[] answers = [new("environment/dotnet-sdk", "10.0.100", null, Recorded),
            new("engine/timeouts/discoveryMinutes", "3", "  measured by hand  ", Recorded.AddSeconds(1)),
            new("engine/timeouts/defaultMinutes", "25", "default", Recorded)];
        OwnerAnswerFile.Write(fixture.Root, answers);

        Assert.Equal(answers.OrderBy(answer => answer.FactKey, StringComparer.Ordinal),
            OwnerAnswerFile.Read(fixture.Root));
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(fixture.Root, OwnerAnswerFile.FileName)));
        Assert.Equal(1, json.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(new[] { "engine/timeouts/defaultMinutes", "engine/timeouts/discoveryMinutes", "environment/dotnet-sdk" },
            json.RootElement.GetProperty("answers").EnumerateArray().Select(entry => entry.GetProperty("factKey").GetString()));
        Assert.Empty(Directory.GetFiles(fixture.Root, ".owner-answers-*.tmp"));
    }

    [Fact]
    public void Write_RepeatedKey_KeepsLaterAnswer()
    {
        using var fixture = new ProjectOnboardingFixture("no-solution");
        var first = new OwnerAnswer("engine/timeouts/discoveryMinutes", "3", "first", Recorded);
        var later = new OwnerAnswer(first.FactKey, "4", "later", Recorded.AddSeconds(1));
        OwnerAnswerFile.Write(fixture.Root, [first]);
        OwnerAnswerFile.Write(fixture.Root, [.. OwnerAnswerFile.Read(fixture.Root), later]);
        Assert.Equal(later, Assert.Single(OwnerAnswerFile.Read(fixture.Root)));
    }

    [Fact]
    public void Read_AbsentFile_ReturnsEmptyWithoutCreatingAnything()
    {
        using var fixture = new ProjectOnboardingFixture("no-solution");
        var directory = Path.Combine(fixture.Root, "absent");
        Assert.Empty(OwnerAnswerFile.Read(directory));
        Assert.False(Directory.Exists(directory));
    }

    [Theory]
    [InlineData("{\"schemaVersion\":2,\"answers\":[]}")]
    [InlineData("{")]
    [InlineData("{\"answers\":[]}")]
    [InlineData("{\"schemaVersion\":1,\"answers\":null}")]
    [InlineData("{\"schemaVersion\":1,\"answers\":[null]}")]
    public void Read_UnreadableDocument_NamesAnswerFile(string json)
    {
        using var fixture = new ProjectOnboardingFixture("no-solution");
        var path = Path.Combine(fixture.Root, OwnerAnswerFile.FileName);
        File.WriteAllText(path, json);
        var error = Assert.Throws<InvalidDataException>(() => OwnerAnswerFile.Read(fixture.Root));
        Assert.Contains("owner-answers.json", error.Message);
        Assert.Equal(json, File.ReadAllText(path));
    }

    [Fact]
    public void Read_ExclusivelyLockedFile_ReportsUnreadableFile()
    {
        using var fixture = new ProjectOnboardingFixture("no-solution");
        OwnerAnswerFile.Write(fixture.Root, []);
        using var handle = new FileStream(Path.Combine(fixture.Root, OwnerAnswerFile.FileName),
            FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.Contains("owner-answers.json",
            Assert.Throws<InvalidDataException>(() => OwnerAnswerFile.Read(fixture.Root)).Message);
    }

    [Fact]
    public void Write_DestinationIsDirectory_RemovesTemporaryFileAfterFailure()
    {
        using var fixture = new ProjectOnboardingFixture("no-solution");
        var destination = Path.Combine(fixture.Root, OwnerAnswerFile.FileName);
        Directory.CreateDirectory(destination);
        var marker = Path.Combine(destination, "preserve.txt");
        File.WriteAllText(marker, "preserve");
        var error = Record.Exception(() => OwnerAnswerFile.Write(fixture.Root,
            [new("engine/timeouts/discoveryMinutes", "3", null, Recorded)]));
        Assert.NotNull(error);
        Assert.True(error is IOException or UnauthorizedAccessException, error.ToString());
        Assert.Empty(Directory.GetFiles(fixture.Root, ".owner-answers-*.tmp"));
        Assert.Equal("preserve", File.ReadAllText(marker));
    }

    [Theory]
    [InlineData("", "3")]
    [InlineData(" ", "3")]
    [InlineData("key", "")]
    [InlineData("key", " ")]
    public void Constructor_BlankIdentityOrValue_RejectsAnswer(string key, string value) =>
        Assert.Throws<ArgumentException>(() => new OwnerAnswer(key, value, null, Recorded));

    [Fact]
    public void Constructor_NonUtcOffset_RejectsAnswer() =>
        Assert.Throws<ArgumentException>(() => new OwnerAnswer("key", "value", null,
            Recorded.ToOffset(TimeSpan.FromHours(1))));
}
