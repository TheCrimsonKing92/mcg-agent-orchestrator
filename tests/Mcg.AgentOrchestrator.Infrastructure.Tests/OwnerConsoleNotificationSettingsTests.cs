using Mcg.AgentOrchestrator.App.OwnerConsole;

// Parallel-safe: each IO test owns a unique temporary directory and cleans it up.
public sealed class OwnerConsoleNotificationSettingsTests
{
    [Fact]
    public void DefaultsMatchAllThresholdsAndHaveNoSoundFiles()
    {
        var settings = new OwnerConsoleNotificationSettings();
        Assert.Equal(TimeSpan.FromMinutes(30), settings.AttentionThreshold);
        Assert.Equal(TimeSpan.FromMinutes(15), settings.StaleTickThreshold);
        Assert.Equal(TimeSpan.FromSeconds(60), settings.CalmCoalesce);
        Assert.Null(settings.CalmSoundFile);
        Assert.Null(settings.AttentionSoundFile);
        Assert.Null(settings.AlarmSoundFile);
    }

    [Fact]
    public void MissingFileReturnsDefaultsWithoutCreatingFile()
    {
        var root = SharedTestSupport.CreateTempDirectory();
        try
        {
            var path = Path.Combine(root, "missing.json");
            Assert.Equal(new OwnerConsoleNotificationSettings(), OwnerConsoleNotificationSettings.Load(path));
            Assert.False(File.Exists(path));
        }
        finally { SharedTestSupport.RemoveTempDirectory(root); }
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("{broken")]
    [InlineData("[]")]
    [InlineData("null")]
    public void EmptyMalformedAndNonObjectFilesReturnDefaults(string text)
    {
        WithFile(text, path => Assert.Equal(new OwnerConsoleNotificationSettings(), OwnerConsoleNotificationSettings.Load(path)));
    }

    [Fact]
    public void PartialSettingsPreserveOtherDefaultsAndFileText()
    {
        const string text = """{"attentionThresholdMinutes":5,"alarmSoundFile":"C:\\sound.wav"}""";
        WithFile(text, path =>
        {
            Assert.Equal(new OwnerConsoleNotificationSettings()
            {
                AttentionThreshold = TimeSpan.FromMinutes(5), AlarmSoundFile = @"C:\sound.wav"
            }, OwnerConsoleNotificationSettings.Load(path));
            Assert.Equal(text, File.ReadAllText(path));
        });
    }

    [Fact]
    public void CaseInsensitiveKeysAndInvalidIndividualValuesUseFieldDefaults()
    {
        WithFile("""{"ATTENTIONTHRESHOLDMINUTES":5,"staleTickThresholdMinutes":-2,"calmCoalesceSeconds":"bad","calmSoundFile":" ","attentionSoundFile":7}""", path =>
            Assert.Equal(new OwnerConsoleNotificationSettings { AttentionThreshold = TimeSpan.FromMinutes(5) },
                OwnerConsoleNotificationSettings.Load(path)));
        WithFile("""{"attentionThresholdMinutes":0,"staleTickThresholdMinutes":2,"calmCoalesceSeconds":10,"calmSoundFile":"calm.wav","attentionSoundFile":"attention.wav"}""", path =>
            Assert.Equal(new OwnerConsoleNotificationSettings
            {
                StaleTickThreshold = TimeSpan.FromMinutes(2), CalmCoalesce = TimeSpan.FromSeconds(10),
                CalmSoundFile = "calm.wav", AttentionSoundFile = "attention.wav"
            }, OwnerConsoleNotificationSettings.Load(path)));
        WithFile("""{"attentionThresholdMinutes":1e300,"staleTickThresholdMinutes":null,"calmCoalesceSeconds":-1}""", path =>
            Assert.Equal(new OwnerConsoleNotificationSettings(), OwnerConsoleNotificationSettings.Load(path)));
    }

    private static void WithFile(string text, Action<string> assertion)
    {
        var root = SharedTestSupport.CreateTempDirectory();
        try
        {
            var path = Path.Combine(root, "settings.json");
            File.WriteAllText(path, text);
            assertion(path);
        }
        finally { SharedTestSupport.RemoveTempDirectory(root); }
    }
}
