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
    public void Compare_ReturnsAddedRemovedAndChangedItemIconFiles()
    {
        WriteBinaryFile("2.6", "Data/ItemIcons/changed.png", [1, 2, 3]);
        WriteBinaryFile("3.0", "Data/ItemIcons/changed.png", [1, 2, 4]);
        WriteBinaryFile("2.6", "Data/ItemIcons/removed.png", [5, 6]);
        WriteBinaryFile("3.0", "Data/ItemIcons/added.png", [7, 8, 9]);
        WriteFile("2.6", "readme.txt", "old");
        WriteFile("3.0", "readme.txt", "new");

        var comparison = Compare();

        Assert.Equal(3, comparison.ChangedFiles.Count);

        var changedIcon = Assert.Single(comparison.ChangedFiles, file => file.RelativePath == "Data/ItemIcons/changed.png");
        Assert.Equal(FileChangeType.Modified, changedIcon.ChangeType);
        Assert.Equal(FileComparisonKind.BinaryAsset, changedIcon.ComparisonKind);
        Assert.Contains(changedIcon.Lines, line => line.Text.Contains("Binary asset changed"));

        var addedIcon = Assert.Single(comparison.ChangedFiles, file => file.RelativePath == "Data/ItemIcons/added.png");
        Assert.Equal(FileChangeType.Added, addedIcon.ChangeType);
        Assert.Equal(FileComparisonKind.BinaryAsset, addedIcon.ComparisonKind);

        var removedIcon = Assert.Single(comparison.ChangedFiles, file => file.RelativePath == "Data/ItemIcons/removed.png");
        Assert.Equal(FileChangeType.Removed, removedIcon.ChangeType);
        Assert.Equal(FileComparisonKind.BinaryAsset, removedIcon.ComparisonKind);
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
    public void Compare_HidesWhitespaceOnlyReplacementLinesWhenContentAlsoChanged()
    {
        WriteFile("2.6", "Config/blocks.xml", """
            <blocks>
              <block name="resourceRockSmall">
                <property name="Texture" value="1"/>
                <property name="EconomicValue" value="5"/>
                <property name="FilterTags" value="MC_outdoor"/>
              </block>
            </blocks>
            """);
        WriteFile("3.0", "Config/blocks.xml", """
            <blocks>

              <block name="resourceRockSmall">
                    <property name="Texture" value="1" />
                    <property name="EconomicValue" value="5" />
                    <property name="FilterTags" value="MC_outdoor,SC_terrain" />
              </block>
            </blocks>
            """);

        var comparison = Compare(ignoreWhitespaceChanges: true);

        var file = Assert.Single(comparison.ChangedFiles);
        Assert.DoesNotContain(file.Lines, line =>
            line.Kind is DiffLineKind.Added or DiffLineKind.Removed
            && line.Text.Contains("Texture", StringComparison.Ordinal));
        Assert.DoesNotContain(file.Lines, line =>
            line.Kind is DiffLineKind.Added or DiffLineKind.Removed
            && line.Text.Contains("EconomicValue", StringComparison.Ordinal));
        var textureLine = Assert.Single(file.Lines, line =>
            line.Kind == DiffLineKind.Context
            && line.Text.Contains("Texture", StringComparison.Ordinal));
        Assert.DoesNotContain(" />", textureLine.OldText, StringComparison.Ordinal);
        Assert.Contains(" />", textureLine.NewText, StringComparison.Ordinal);
        Assert.Contains(file.Lines, line =>
            line.Kind == DiffLineKind.Removed
            && line.Text.Contains("MC_outdoor", StringComparison.Ordinal));
        Assert.Contains(file.Lines, line =>
            line.Kind == DiffLineKind.Added
            && line.Text.Contains("MC_outdoor,SC_terrain", StringComparison.Ordinal));
    }

    [Fact]
    public void Compare_HidesWhitespaceOnlyLinesShiftedByInsertedContent()
    {
        WriteFile("2.6", "Config/blocks.xml", """
            <blocks>
              <block name="terrDestroyedWoodDebris">
                <property name="DisplayType" value="blockTerrain"/>
                <property name="Map.Color" value="110,95,49"/>
                <property name="Material" value="Mwood_regular"/>
                <property name="FuelValue" value="150"/>
                <property name="MaxDamage" value="30"/>
                <dropextendsoff/>
                <drop event="Harvest" name="resourceWood" count="1,2" tag="oreWoodHarvest,lumberjackHarvest"/>
                <drop event="Destroy" count="0"/>
                <property name="SoundPickup" value="wooddebrisblock_grab"/>
                <property name="SoundPlace" value="wooddebrisblock_place"/>
                <property name="SortOrder2" value="0750"/>
              </block>
            </blocks>
            """);
        WriteFile("3.0", "Config/blocks.xml", """
            <blocks>
              <block name="terrDestroyedWoodDebris">
                <property name="DisplayType" value="blockTerrain" />
                <property name="MapColor" value="110,95,49" />
                <property name="Texture" value="439" />
                <property name="Material" value="Mwood_regular" />
                <property name="FuelValue" value="150" />
                <property name="MaxDamage" value="30" />
                <dropextendsoff />
                <drop event="Harvest" name="resourceWood" count="1,2" tag="oreWoodHarvest,lumberjackHarvest" />
                <drop event="Destroy" count="0" />
                <property name="SoundPickup" value="wooddebrisblock_grab" />
                <property name="SoundPlace" value="wooddebrisblock_place" />
                <property name="SortOrder2" value="0750" />
              </block>
            </blocks>
            """);

        var comparison = Compare(ignoreWhitespaceChanges: true);

        var file = Assert.Single(comparison.ChangedFiles);
        Assert.Equal(2, file.Additions);
        Assert.Equal(1, file.Deletions);
        Assert.Contains(file.Lines, line =>
            line.Kind == DiffLineKind.Removed
            && line.Text.Contains("Map.Color", StringComparison.Ordinal));
        var mapColorLine = Assert.Single(file.Lines, line =>
            line.Kind == DiffLineKind.Added
            && line.Text.Contains("MapColor", StringComparison.Ordinal));
        var textureLine = Assert.Single(file.Lines, line =>
            line.Kind == DiffLineKind.Added
            && line.Text.Contains("Texture", StringComparison.Ordinal));
        Assert.Contains(" />", mapColorLine.Text, StringComparison.Ordinal);
        Assert.Contains(" />", textureLine.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(file.Lines, line =>
            line.Kind is DiffLineKind.Added or DiffLineKind.Removed
            && line.Text.Contains("Material", StringComparison.Ordinal));
        Assert.DoesNotContain(file.Lines, line =>
            line.Kind is DiffLineKind.Added or DiffLineKind.Removed
            && line.Text.Contains("SortOrder2", StringComparison.Ordinal));
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
    public void Compare_PreservesSideSpecificWhitespaceRowsWhenLoadedFromCache()
    {
        WriteFile("2.6", "Config/blocks.xml", """
            <blocks>
              <block name="resourceRockSmall">
                <property name="Texture" value="1"/>
                <property name="FilterTags" value="MC_outdoor"/>
              </block>
            </blocks>
            """);
        WriteFile("3.0", "Config/blocks.xml", """
            <blocks>
              <block name="resourceRockSmall">
                <property name="Texture" value="1" />
                <property name="FilterTags" value="MC_outdoor,SC_terrain" />
              </block>
            </blocks>
            """);

        var firstComparison = Compare(ignoreWhitespaceChanges: true);
        var secondComparison = Compare(ignoreWhitespaceChanges: true);

        Assert.False(firstComparison.IsFromCache);
        Assert.True(secondComparison.IsFromCache);
        var textureLine = Assert.Single(secondComparison.ChangedFiles.Single().Lines, line =>
            line.Kind == DiffLineKind.Context
            && line.Text.Contains("Texture", StringComparison.Ordinal));
        Assert.DoesNotContain(" />", textureLine.OldText, StringComparison.Ordinal);
        Assert.Contains(" />", textureLine.NewText, StringComparison.Ordinal);
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

    [Fact]
    public void Compare_DisabledCacheAlwaysRecomputesComparison()
    {
        WriteFile("2.6", "Config/items.xml", "<items><item name=\"old\" /></items>");
        WriteFile("3.0", "Config/items.xml", "<items><item name=\"new\" /></items>");

        var service = new XmlVersionComparisonService(DisabledVersionComparisonCache.Instance);

        var firstComparison = service.Compare(
            "2.6",
            "3.0",
            Path.Combine(_root, "2.6"),
            Path.Combine(_root, "3.0"));

        WriteFile("3.0", "Config/items.xml", "<items><item name=\"newer-value\" /></items>");

        var recomputedComparison = service.Compare(
            "2.6",
            "3.0",
            Path.Combine(_root, "2.6"),
            Path.Combine(_root, "3.0"));

        Assert.True(firstComparison.IsCacheDisabled);
        Assert.False(firstComparison.IsFromCache);
        Assert.True(recomputedComparison.IsCacheDisabled);
        Assert.False(recomputedComparison.IsFromCache);
        var file = Assert.Single(recomputedComparison.ChangedFiles);
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

    private void WriteBinaryFile(string version, string relativePath, byte[] bytes)
    {
        var path = Path.Combine(_root, version, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
    }

    private void WriteModFile(string modName, string relativePath, string text)
    {
        var path = Path.Combine(_root, "Mods", modName, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text.ReplaceLineEndings(Environment.NewLine));
    }
}
