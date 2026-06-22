using VersionCompareTool.Core;

namespace VersionCompareTool.Core.Tests;

public sealed class XmlVersionComparisonServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
    private readonly string _cacheRoot;

    public XmlVersionComparisonServiceTests()
    {
        _cacheRoot = Path.Combine(_root, "Cache");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void Compare_ReturnsModifiedXmlFilesWithAddedAndRemovedLines()
    {
        WriteFile("2.6", "config/items.xml", """
            <items>
              <item name="meleeToolFireaxeIron" damage="38" />
            </items>
            """);
        WriteFile("3.0", "config/items.xml", """
            <items>
              <item name="meleeToolFireaxeIron" damage="42" />
              <item name="meleeToolFireaxeSteel" damage="55" />
            </items>
            """);

        var comparison = Compare();

        var file = Assert.Single(comparison.ChangedFiles);
        Assert.Equal("config/items.xml", file.RelativePath);
        Assert.Equal(FileChangeType.Modified, file.ChangeType);
        Assert.Contains(file.Lines, line => line.Kind == DiffLineKind.Removed && line.Text.Contains("damage=\"38\""));
        Assert.Contains(file.Lines, line => line.Kind == DiffLineKind.Added && line.Text.Contains("damage=\"42\""));
        Assert.Contains(file.Lines, line => line.Kind == DiffLineKind.Added && line.Text.Contains("meleeToolFireaxeSteel"));
    }

    [Fact]
    public void Compare_ReturnsAddedAndRemovedXmlFiles()
    {
        WriteFile("2.6", "config/loot.xml", """
            <lootcontainers>
              <lootgroup name="oldGroup" />
            </lootcontainers>
            """);
        WriteFile("3.0", "config/recipes.xml", """
            <recipes>
              <recipe name="resourceForgedSteel" />
            </recipes>
            """);

        var comparison = Compare();

        var addedFile = Assert.Single(comparison.ChangedFiles, file => file.ChangeType == FileChangeType.Added);
        var removedFile = Assert.Single(comparison.ChangedFiles, file => file.ChangeType == FileChangeType.Removed);

        Assert.Equal("config/recipes.xml", addedFile.RelativePath);
        Assert.Equal(3, addedFile.Additions);
        Assert.Equal("config/loot.xml", removedFile.RelativePath);
        Assert.Equal(3, removedFile.Deletions);
    }

    [Fact]
    public void Compare_IgnoresUnchangedFilesAndNonXmlFiles()
    {
        WriteFile("2.6", "config/blocks.xml", "<blocks />");
        WriteFile("3.0", "config/blocks.xml", "<blocks />");
        WriteFile("2.6", "readme.txt", "old");
        WriteFile("3.0", "readme.txt", "new");

        var comparison = Compare();

        Assert.Empty(comparison.ChangedFiles);
    }

    [Fact]
    public void Compare_CanIgnoreWhitespaceOnlyChanges()
    {
        WriteFile("2.6", "config/items.xml", """
            <items>
              <item name="old" />
            </items>
            """);
        WriteFile("3.0", "config/items.xml", """
            <items>
                    <item name="old" />
            </items>
            """);

        var comparison = Compare(ignoreWhitespaceChanges: true);

        Assert.Empty(comparison.ChangedFiles);
    }

    [Fact]
    public void Compare_CanIgnoreBlankLineOnlyChanges()
    {
        WriteFile("2.6", "config/items.xml", """
            <items>
              <item name="old" />
            </items>
            """);
        WriteFile("3.0", "config/items.xml", """
            <items>

              <item name="old" />
            </items>
            """);

        var comparison = Compare(ignoreWhitespaceChanges: true);

        Assert.Empty(comparison.ChangedFiles);
    }

    [Fact]
    public void Compare_HidesBlankAddedLinesWhenContentAlsoChanged()
    {
        WriteFile("2.6", "config/archetypes.xml", """
            <archetypes>
            </archetypes>
            """);
        WriteFile("3.0", "config/archetypes.xml", """
            <?xml version="1.0" encoding="UTF-8"?>

            <archetypes>
            </archetypes>
            """);

        var comparison = Compare(ignoreWhitespaceChanges: true);

        var file = Assert.Single(comparison.ChangedFiles);
        Assert.Equal(1, file.Additions);
        Assert.DoesNotContain(file.Lines, line => line.Kind == DiffLineKind.Added && string.IsNullOrWhiteSpace(line.Text));
        Assert.Contains(file.Lines, line => line.Kind == DiffLineKind.Added && line.Text.Contains("<?xml"));
    }

    [Fact]
    public void Compare_UsesSeparateCacheEntriesForWhitespaceSetting()
    {
        WriteFile("2.6", "config/items.xml", """
            <items>
              <item name="old" />
            </items>
            """);
        WriteFile("3.0", "config/items.xml", """
            <items>
                    <item name="old" />
            </items>
            """);

        var whitespaceAwareComparison = Compare();
        var ignoreWhitespaceComparison = Compare(ignoreWhitespaceChanges: true);

        Assert.Single(whitespaceAwareComparison.ChangedFiles);
        Assert.Empty(ignoreWhitespaceComparison.ChangedFiles);
        Assert.False(ignoreWhitespaceComparison.IsFromCache);
    }

    [Fact]
    public void Compare_FlagsModFilesThatOverlapChangedVersionFiles()
    {
        WriteFile("2.6", "Data/Config/items.xml", """
            <items>
              <item name="meleeToolFireaxeIron" damage="38" />
            </items>
            """);
        WriteFile("3.0", "Data/Config/items.xml", """
            <items>
              <item name="meleeToolFireaxeIron" damage="42" />
            </items>
            """);
        WriteModFile("BetterAxes", "Config/items.xml", """
            <configs>
              <set xpath="/items/item[@name='meleeToolFireaxeIron']/@damage">46</set>
            </configs>
            """);

        var comparison = CompareWithMod("BetterAxes");

        var file = Assert.Single(comparison.ChangedFiles);
        var conflict = Assert.Single(file.ModConflicts);
        Assert.Equal("BetterAxes", conflict.ModName);
        Assert.Equal("Config/items.xml", conflict.ModRelativePath);
        Assert.Equal("set", conflict.Operation);
        Assert.Equal("/items/item[@name='meleeToolFireaxeIron']/@damage", conflict.XPath);
        Assert.Equal(1, comparison.ModConflictFiles);
        Assert.Equal(1, comparison.TotalModConflicts);
    }

    [Fact]
    public void Compare_DoesNotFlagModFilesThatDoNotOverlapChangedVersionFiles()
    {
        WriteFile("2.6", "Config/items.xml", "<items><item name=\"old\" /></items>");
        WriteFile("3.0", "Config/items.xml", "<items><item name=\"new\" /></items>");
        WriteModFile("BetterLoot", "Config/loot.xml", "<configs />");

        var comparison = CompareWithMod("BetterLoot");

        var file = Assert.Single(comparison.ChangedFiles);
        Assert.Empty(file.ModConflicts);
        Assert.Equal(0, comparison.ModConflictFiles);
    }

    [Fact]
    public void Compare_DoesNotFlagUnrelatedXPathPatchInChangedFile()
    {
        WriteFile("2.6", "Config/items.xml", """
            <items>
              <item name="changed" damage="38" />
              <item name="untouched" damage="10" />
            </items>
            """);
        WriteFile("3.0", "Config/items.xml", """
            <items>
              <item name="changed" damage="42" />
              <item name="untouched" damage="10" />
            </items>
            """);
        WriteModFile("BetterAxes", "Config/items.xml", """
            <configs>
              <set xpath="/items/item[@name='untouched']/@damage">15</set>
            </configs>
            """);

        var comparison = CompareWithMod("BetterAxes");

        var file = Assert.Single(comparison.ChangedFiles);
        Assert.Empty(file.ModConflicts);
        Assert.Equal(0, comparison.ModConflictFiles);
    }

    [Fact]
    public void ApplyModConflicts_ReusesExistingComparisonWithoutChangingDiffLines()
    {
        WriteFile("2.6", "Config/items.xml", "<items><item name=\"old\" /></items>");
        WriteFile("3.0", "Config/items.xml", "<items><item name=\"new\" /></items>");
        WriteModFile("BetterAxes", "Config/items.xml", """
            <configs>
              <set xpath="/items/item[@name='old']">anything</set>
            </configs>
            """);

        var baseComparison = Compare();
        var service = CreateService();

        var comparison = service.ApplyModConflicts(
            baseComparison,
            "BetterAxes",
            Path.Combine(_root, "Mods", "BetterAxes"));

        var file = Assert.Single(comparison.ChangedFiles);
        Assert.Same(baseComparison.ChangedFiles[0].Lines, file.Lines);
        Assert.Single(file.ModConflicts);
        Assert.Equal("BetterAxes", comparison.ModName);
    }

    [Fact]
    public void Compare_LoadsCachedComparisonWhenVersionFoldersAreUnchanged()
    {
        WriteFile("2.6", "Config/items.xml", "<items><item name=\"old\" /></items>");
        WriteFile("3.0", "Config/items.xml", "<items><item name=\"new\" /></items>");

        var firstComparison = Compare();
        var secondComparison = Compare();

        Assert.False(firstComparison.IsFromCache);
        Assert.True(secondComparison.IsFromCache);
        Assert.Equal(
            firstComparison.ChangedFiles.Single().Lines.Select(line => line.Text),
            secondComparison.ChangedFiles.Single().Lines.Select(line => line.Text));
    }

    [Fact]
    public void Compare_RefreshesCachedComparisonWhenVersionFolderChanges()
    {
        WriteFile("2.6", "Config/items.xml", "<items><item name=\"old\" /></items>");
        WriteFile("3.0", "Config/items.xml", "<items><item name=\"new\" /></items>");

        _ = Compare();

        WriteFile("3.0", "Config/items.xml", "<items><item name=\"newer-value\" /></items>");

        var refreshedComparison = Compare();

        Assert.False(refreshedComparison.IsFromCache);
        var file = Assert.Single(refreshedComparison.ChangedFiles);
        Assert.Contains(file.Lines, line => line.Kind == DiffLineKind.Added && line.Text.Contains("newer-value"));
    }

    private VersionComparison Compare(bool ignoreWhitespaceChanges = false)
    {
        var service = CreateService();
        return service.Compare(
            "2.6",
            "3.0",
            Path.Combine(_root, "2.6"),
            Path.Combine(_root, "3.0"),
            ignoreWhitespaceChanges: ignoreWhitespaceChanges);
    }

    private VersionComparison CompareWithMod(string modName)
    {
        var service = CreateService();
        return service.Compare(
            "2.6",
            "3.0",
            Path.Combine(_root, "2.6"),
            Path.Combine(_root, "3.0"),
            modName,
            Path.Combine(_root, "Mods", modName));
    }

    private XmlVersionComparisonService CreateService()
    {
        return new XmlVersionComparisonService(new FileVersionComparisonCache(_cacheRoot));
    }

    private void WriteFile(string version, string relativePath, string text)
    {
        var path = Path.Combine(_root, version, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text.ReplaceLineEndings(Environment.NewLine));
    }

    private void WriteModFile(string modName, string relativePath, string text)
    {
        var path = Path.Combine(_root, "Mods", modName, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text.ReplaceLineEndings(Environment.NewLine));
    }
}
