using VersionCompareTool.ViewModels;

namespace VersionCompareTool.Core.Tests;

public sealed class AppSettingsStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void Save_ThenLoad_RoundTripsSelectedSettings()
    {
        var store = CreateStore();
        var settings = new AppSettings
        {
            SelectedStartVersion = "2.6",
            SelectedEndVersion = "3.0",
            SelectedModName = "ExampleMod",
            IsFolderView = true,
            IsSideBySideDiffView = false,
            ShowOnlyModConflicts = true,
            IgnoreWhitespaceChanges = false
        };

        store.Save(settings);
        var loadedSettings = store.Load();

        Assert.Equal("2.6", loadedSettings.SelectedStartVersion);
        Assert.Equal("3.0", loadedSettings.SelectedEndVersion);
        Assert.Equal("ExampleMod", loadedSettings.SelectedModName);
        Assert.True(loadedSettings.IsFolderView);
        Assert.False(loadedSettings.IsSideBySideDiffView);
        Assert.True(loadedSettings.ShowOnlyModConflicts);
        Assert.False(loadedSettings.IgnoreWhitespaceChanges);
    }

    [Fact]
    public void Load_WithCorruptSettings_ReturnsDefaults()
    {
        var settingsPath = Path.Combine(_root, "settings.json");
        Directory.CreateDirectory(_root);
        File.WriteAllText(settingsPath, "{not valid json");

        var settings = new AppSettingsStore(settingsPath).Load();

        Assert.Null(settings.SelectedStartVersion);
        Assert.Null(settings.SelectedEndVersion);
        Assert.Null(settings.SelectedModName);
        Assert.False(settings.IsFolderView);
        Assert.True(settings.IsSideBySideDiffView);
        Assert.False(settings.ShowOnlyModConflicts);
        Assert.True(settings.IgnoreWhitespaceChanges);
    }

    private AppSettingsStore CreateStore()
    {
        return new AppSettingsStore(Path.Combine(_root, "settings.json"));
    }
}
