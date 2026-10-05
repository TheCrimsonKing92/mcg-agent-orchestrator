using System.Text.Json.Nodes;
using Mcg.AgentOrchestrator.Core.Conductor;

namespace Mcg.AgentOrchestrator.Core.Tests;

public sealed class ConductorAutonomyPolicyTestsBoardFill
{
    [Fact]
    public void Defaults_and_legacy_file_load_use_shadow_ten_and_three()
    {
        var original = ConductorAutonomyPolicy.Permissive with { Name = "Legacy board fill" };
        AssertDefaults(original);
        var json = JsonNode.Parse(original.ToJson())!.AsObject();
        json.Remove("boardFillMode");
        json.Remove("boardFillTargetActiveGoals");
        json.Remove("boardFillMaxDraftsPerDay");
        AssertDefaults(ConductorAutonomyPolicy.ParseJson(json.ToJsonString()));
        // Isolated directory, no shared policy or database.
        var directory = Path.Combine(Path.GetTempPath(), "board-fill-policy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Combine(directory, "conductor-policy.json"), json.ToJsonString());
            var loaded = ConductorAutonomyPolicy.LoadFromOrchestratorDirectory(new DirectoryInfo(directory));
            AssertDefaults(loaded);
            Assert.Equal(original.Name, loaded.Name);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Theory]
    [InlineData(ConductorBoardFillMode.Off, 0, 0)]
    [InlineData(ConductorBoardFillMode.Shadow, 10, 3)]
    [InlineData(ConductorBoardFillMode.File, 30, 20)]
    public void Board_fill_values_round_trip(ConductorBoardFillMode mode, int target, int cap)
    {
        var policy = ConductorAutonomyPolicy.Permissive with
        {
            BoardFillMode = mode, BoardFillTargetActiveGoals = target, BoardFillMaxDraftsPerDay = cap
        };
        Assert.Empty(policy.Validate());
        var roundTrip = ConductorAutonomyPolicy.ParseJson(policy.ToJson());
        Assert.Equal(mode, roundTrip.BoardFillMode);
        Assert.Equal(target, roundTrip.BoardFillTargetActiveGoals);
        Assert.Equal(cap, roundTrip.BoardFillMaxDraftsPerDay);
    }

    [Theory]
    [InlineData(-1, 3, "boardFillTargetActiveGoals")]
    [InlineData(31, 3, "boardFillTargetActiveGoals")]
    [InlineData(10, -1, "boardFillMaxDraftsPerDay")]
    [InlineData(10, 21, "boardFillMaxDraftsPerDay")]
    public void Invalid_ranges_name_the_parameter(int target, int cap, string name)
    {
        var policy = ConductorAutonomyPolicy.Permissive with
        {
            BoardFillTargetActiveGoals = target, BoardFillMaxDraftsPerDay = cap
        };
        Assert.Contains(policy.Validate(), error => error.Contains(name, StringComparison.Ordinal));
        Assert.Contains(name, Assert.Throws<FormatException>(() => ConductorAutonomyPolicy.ParseJson(policy.ToJson())).Message);
    }

    [Theory]
    [InlineData("\"unknown\"")]
    [InlineData("\"99\"")]
    [InlineData("99")]
    public void Invalid_modes_fail_loudly(string mode)
    {
        var json = ConductorAutonomyPolicy.Permissive.ToJson().Replace("\"boardFillMode\": \"Shadow\"", "\"boardFillMode\": " + mode);
        Assert.Contains("boardFillMode", Assert.Throws<FormatException>(() => ConductorAutonomyPolicy.ParseJson(json)).Message);
        Assert.Contains((ConductorAutonomyPolicy.Permissive with { BoardFillMode = (ConductorBoardFillMode)99 }).Validate(),
            error => error.Contains("boardFillMode", StringComparison.Ordinal));
    }

    private static void AssertDefaults(ConductorAutonomyPolicy policy)
    {
        Assert.Equal(ConductorBoardFillMode.Shadow, policy.BoardFillMode);
        Assert.Equal(10, policy.BoardFillTargetActiveGoals);
        Assert.Equal(3, policy.BoardFillMaxDraftsPerDay);
    }
}
