using System.Text.Json;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: independent stores with identical fixed outcomes and opposite fit notes.
public sealed class CliOwnerDigestFitNoteIndependenceTests
{
    [Fact(DisplayName = "Opposite self reported fit notes leave measured digest rows identical")]
    public async Task WorkerFitNotesCannotChangeMeasuredFit()
    {
        using var adequate = await OwnerDigestTestFixture.CreateAsync();
        using var underpowered = await OwnerDigestTestFixture.CreateAsync();
        await OwnerDigestFitFixture.AddRoundsAsync(adequate, "adequate");
        await OwnerDigestFitFixture.AddRoundsAsync(underpowered, "underpowered");
        var adequateKernel = await new SqliteOrchestratorStateRepository(adequate.Workspace.SqliteStatePath).LoadAsync();
        var underpoweredKernel = await new SqliteOrchestratorStateRepository(underpowered.Workspace.SqliteStatePath).LoadAsync();
        // The shared fixture also seeds tasks without rounds or verification records.
        var adequateTasks = adequateKernel.Goals.SelectMany(goal => goal.Tasks)
            .Where(task => task.LastDispatch is not null).ToArray();
        var underpoweredTasks = underpoweredKernel.Goals.SelectMany(goal => goal.Tasks)
            .Where(task => task.LastDispatch is not null).ToArray();
        Assert.Equal(5, adequateTasks.Length);
        Assert.Equal(5, underpoweredTasks.Length);
        Assert.All(adequateTasks, task =>
        {
            Assert.NotNull(task.LastVerification);
            Assert.Equal("adequate", task.LastVerification.ModelFitNote);
            Assert.Contains("- adequate -", task.LastVerification.StandardOutput);
        });
        Assert.All(underpoweredTasks, task =>
        {
            Assert.NotNull(task.LastVerification);
            Assert.Equal("underpowered", task.LastVerification.ModelFitNote);
            Assert.Contains("- underpowered -", task.LastVerification.StandardOutput);
        });
        using var first = JsonDocument.Parse(OwnerDigestFitFixture.Run(adequate, "--json"));
        using var second = JsonDocument.Parse(OwnerDigestFitFixture.Run(underpowered, "--json"));
        var firstRows = first.RootElement.GetProperty("rounds").GetProperty("fitByRoleModelTaskClass");
        Assert.Equal(4, firstRows.GetArrayLength());
        Assert.Equal(firstRows.GetRawText(),
            second.RootElement.GetProperty("rounds").GetProperty("fitByRoleModelTaskClass").GetRawText());
        Assert.Equal(OwnerDigestFitFixture.FitText(OwnerDigestFitFixture.Run(adequate)),
            OwnerDigestFitFixture.FitText(OwnerDigestFitFixture.Run(underpowered)));
    }
}
