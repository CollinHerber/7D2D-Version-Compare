using DiffPlex.DiffBuilder;
using DiffPlex.DiffBuilder.Model;
using System.Collections;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using System.Xml.XPath;

namespace VersionCompareTool.Core;

public sealed class XmlVersionComparisonService
{
    private static readonly StringComparer PathComparer = StringComparer.OrdinalIgnoreCase;
    private readonly IVersionComparisonCache _cache;
    private readonly bool _isCacheDisabled;

    public XmlVersionComparisonService()
        : this(VersionComparisonCacheFactory.CreateDefault())
    {
    }

    public XmlVersionComparisonService(IVersionComparisonCache cache)
    {
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _isCacheDisabled = ReferenceEquals(cache, DisabledVersionComparisonCache.Instance);
    }

    public VersionComparison Compare(
        string startVersion,
        string endVersion,
        string startDirectory,
        string endDirectory,
        string? modName = null,
        string? modDirectory = null,
        bool ignoreWhitespaceChanges = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(startVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(endVersion);
        cancellationToken.ThrowIfCancellationRequested();

        if (!Directory.Exists(startDirectory))
        {
            throw new DirectoryNotFoundException($"Start version directory was not found: {startDirectory}");
        }

        if (!Directory.Exists(endDirectory))
        {
            throw new DirectoryNotFoundException($"End version directory was not found: {endDirectory}");
        }

        var startDirectoryPath = Path.GetFullPath(startDirectory);
        var endDirectoryPath = Path.GetFullPath(endDirectory);
        var startCatalog = LoadComparableFiles(startDirectoryPath, cancellationToken);
        var endCatalog = LoadComparableFiles(endDirectoryPath, cancellationToken);
        var cacheKey = new VersionComparisonCacheKey(
            startVersion,
            endVersion,
            NormalizeDirectoryForCacheKey(startDirectoryPath),
            NormalizeDirectoryForCacheKey(endDirectoryPath),
            startCatalog.Fingerprint,
            endCatalog.Fingerprint,
            ignoreWhitespaceChanges);

        var cachedComparison = _cache.TryLoad(cacheKey, cancellationToken);
        if (cachedComparison is not null)
        {
            return ApplyModConflicts(
                cachedComparison with
                {
                    StartDirectory = startDirectoryPath,
                    EndDirectory = endDirectoryPath
                },
                modName,
                modDirectory,
                cancellationToken);
        }

        var comparison = BuildComparison(
            startVersion,
            endVersion,
            startCatalog.Files,
            endCatalog.Files,
            ignoreWhitespaceChanges,
            cancellationToken) with
        {
            StartDirectory = startDirectoryPath,
            EndDirectory = endDirectoryPath,
            IsCacheDisabled = _isCacheDisabled
        };

        _cache.Save(cacheKey, comparison, cancellationToken);
        return ApplyModConflicts(comparison, modName, modDirectory, cancellationToken);
    }

    public VersionComparison ApplyModConflicts(
        VersionComparison comparison,
        string? modName,
        string? modDirectory,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(modName))
        {
            var clearedFiles = comparison.ChangedFiles
                .Select(file => file.WithModConflicts([]))
                .ToArray();

            return comparison with
            {
                ChangedFiles = clearedFiles,
                ModName = null
            };
        }

        var modPatchIndex = LoadModPatchIndex(modName, modDirectory, cancellationToken);
        var changedNodeIndexes = new Dictionary<string, XmlChangedNodeIndex>(PathComparer);
        var changedFiles = comparison.ChangedFiles
            .Select(file =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return file.WithModConflicts(FindModConflicts(
                    file,
                    modPatchIndex,
                    comparison,
                    changedNodeIndexes,
                    cancellationToken));
            })
            .ToArray();

        return comparison with
        {
            ChangedFiles = changedFiles,
            ModName = modName
        };
    }

    private static VersionComparison BuildComparison(
        string startVersion,
        string endVersion,
        SortedDictionary<string, string> startFiles,
        SortedDictionary<string, string> endFiles,
        bool ignoreWhitespaceChanges,
        CancellationToken cancellationToken)
    {
        var allPaths = startFiles.Keys
            .Union(endFiles.Keys, PathComparer)
            .Order(PathComparer)
            .ToArray();

        var changedFiles = new List<ChangedFile>();

        foreach (var relativePath in allPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var hasStartFile = startFiles.TryGetValue(relativePath, out var startPath);
            var hasEndFile = endFiles.TryGetValue(relativePath, out var endPath);
            var comparisonKind = GetComparisonKind(relativePath);

            if (!hasStartFile && hasEndFile && endPath is not null)
            {
                changedFiles.Add(BuildWholeFileChange(
                    relativePath,
                    endPath,
                    FileChangeType.Added,
                    comparisonKind));
                continue;
            }

            if (hasStartFile && startPath is not null && !hasEndFile)
            {
                changedFiles.Add(BuildWholeFileChange(
                    relativePath,
                    startPath,
                    FileChangeType.Removed,
                    comparisonKind));
                continue;
            }

            if (startPath is null || endPath is null)
            {
                continue;
            }

            if (comparisonKind == FileComparisonKind.BinaryAsset)
            {
                if (FilesHaveSameContent(startPath, endPath))
                {
                    continue;
                }

                changedFiles.Add(BuildBinaryAssetChange(
                    relativePath,
                    FileChangeType.Modified,
                    startPath,
                    endPath));
                continue;
            }

            var startText = File.ReadAllText(startPath);
            cancellationToken.ThrowIfCancellationRequested();
            var endText = File.ReadAllText(endPath);
            if (string.Equals(startText, endText, StringComparison.Ordinal))
            {
                continue;
            }

            cancellationToken.ThrowIfCancellationRequested();
            var lines = BuildModifiedFileDiff(startText, endText, ignoreWhitespaceChanges);
            if (!lines.Any(line => line.Kind is DiffLineKind.Added or DiffLineKind.Removed))
            {
                continue;
            }

            changedFiles.Add(new ChangedFile(
                relativePath,
                FileChangeType.Modified,
                lines.Count(line => line.Kind == DiffLineKind.Added),
                lines.Count(line => line.Kind == DiffLineKind.Removed),
                lines));
        }

        return new VersionComparison(startVersion, endVersion, changedFiles);
    }

    private static XmlFileCatalog LoadComparableFiles(
        string rootDirectory,
        CancellationToken cancellationToken)
    {
        var files = new SortedDictionary<string, string>(PathComparer);
        var entries = new SortedDictionary<string, XmlFileEntry>(PathComparer);

        foreach (var filePath in EnumerateComparableFilePaths(rootDirectory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relativePath = NormalizeRelativePath(Path.GetRelativePath(rootDirectory, filePath));
            var fileInfo = new FileInfo(filePath);
            entries[relativePath] = new XmlFileEntry(
                filePath,
                fileInfo.Length,
                fileInfo.LastWriteTimeUtc.Ticks);
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long totalBytes = 0;
        long latestWriteUtcTicks = 0;

        foreach (var (relativePath, entry) in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            files[relativePath] = entry.FullPath;
            totalBytes += entry.Length;
            latestWriteUtcTicks = Math.Max(latestWriteUtcTicks, entry.LastWriteUtcTicks);

            AppendHashData(hash, relativePath);
            AppendHashData(hash, entry.Length.ToString(CultureInfo.InvariantCulture));
            AppendHashData(hash, entry.LastWriteUtcTicks.ToString(CultureInfo.InvariantCulture));
        }

        var fingerprint = new FolderFingerprint(
            files.Count,
            totalBytes,
            latestWriteUtcTicks,
            Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant());

        return new XmlFileCatalog(files, fingerprint);
    }

    private static ChangedFile BuildWholeFileChange(
        string relativePath,
        string filePath,
        FileChangeType changeType,
        FileComparisonKind comparisonKind)
    {
        if (comparisonKind == FileComparisonKind.BinaryAsset)
        {
            return BuildBinaryAssetChange(relativePath, changeType, filePath, filePath);
        }

        var text = File.ReadAllText(filePath);
        var kind = changeType == FileChangeType.Added ? DiffLineKind.Added : DiffLineKind.Removed;
        var lines = SplitLines(text)
            .Select((line, index) => new DiffLine(
                kind == DiffLineKind.Removed ? index + 1 : null,
                kind == DiffLineKind.Added ? index + 1 : null,
                kind,
                line))
            .ToArray();

        return new ChangedFile(
            relativePath,
            changeType,
            changeType == FileChangeType.Added ? lines.Length : 0,
            changeType == FileChangeType.Removed ? lines.Length : 0,
            lines);
    }

    private static ChangedFile BuildBinaryAssetChange(
        string relativePath,
        FileChangeType changeType,
        string startPath,
        string endPath)
    {
        var summary = changeType switch
        {
            FileChangeType.Added => $"Added binary asset: {FormatFileSize(new FileInfo(endPath).Length)}",
            FileChangeType.Removed => $"Removed binary asset: {FormatFileSize(new FileInfo(startPath).Length)}",
            _ => $"Binary asset changed: {FormatFileSize(new FileInfo(startPath).Length)} -> {FormatFileSize(new FileInfo(endPath).Length)}"
        };

        return new ChangedFile(
            relativePath,
            changeType,
            0,
            0,
            [new DiffLine(null, null, DiffLineKind.Context, summary)],
            FileComparisonKind.BinaryAsset);
    }

    private static IEnumerable<string> EnumerateComparableFilePaths(string rootDirectory)
    {
        var filePaths = new HashSet<string>(PathComparer);

        foreach (var xmlPath in Directory.EnumerateFiles(rootDirectory, "*.xml", SearchOption.AllDirectories))
        {
            filePaths.Add(xmlPath);
        }

        foreach (var itemIconsDirectory in EnumerateItemIconsDirectories(rootDirectory))
        {
            foreach (var assetPath in Directory.EnumerateFiles(itemIconsDirectory, "*", SearchOption.AllDirectories))
            {
                filePaths.Add(assetPath);
            }
        }

        return filePaths.Order(PathComparer);
    }

    private static IEnumerable<string> EnumerateItemIconsDirectories(string rootDirectory)
    {
        if (IsItemIconsDirectory(rootDirectory))
        {
            yield return rootDirectory;
        }

        foreach (var directory in Directory.EnumerateDirectories(rootDirectory, "ItemIcons", SearchOption.AllDirectories))
        {
            yield return directory;
        }
    }

    private static bool IsItemIconsDirectory(string path)
    {
        return string.Equals(
            Path.GetFileName(Path.TrimEndingDirectorySeparator(path)),
            "ItemIcons",
            StringComparison.OrdinalIgnoreCase);
    }

    private static Dictionary<string, List<ModPatchOperation>> LoadModPatchIndex(
        string? modName,
        string? modDirectory,
        CancellationToken cancellationToken)
    {
        var index = new Dictionary<string, List<ModPatchOperation>>(PathComparer);

        if (string.IsNullOrWhiteSpace(modName)
            || string.IsNullOrWhiteSpace(modDirectory)
            || !Directory.Exists(modDirectory))
        {
            return index;
        }

        foreach (var filePath in Directory.EnumerateFiles(modDirectory, "*.xml", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relativePath = NormalizeRelativePath(Path.GetRelativePath(modDirectory, filePath));
            var targetPath = NormalizeTargetPath(relativePath);
            var operations = LoadModPatchOperations(modName, relativePath, targetPath, filePath);
            if (operations.Count == 0)
            {
                continue;
            }

            if (!index.TryGetValue(targetPath, out var patches))
            {
                patches = [];
                index[targetPath] = patches;
            }

            patches.AddRange(operations);
        }

        return index;
    }

    private static IReadOnlyList<ModPatchOperation> LoadModPatchOperations(
        string modName,
        string modRelativePath,
        string targetPath,
        string filePath)
    {
        try
        {
            var document = XDocument.Load(filePath, LoadOptions.PreserveWhitespace);
            return document.Descendants()
                .Select(element => new
                {
                    Element = element,
                    XPath = element.Attribute("xpath")?.Value
                })
                .Where(patch => !string.IsNullOrWhiteSpace(patch.XPath))
                .Select(patch => new ModPatchOperation(
                    modName,
                    modRelativePath,
                    targetPath,
                    patch.Element.Name.LocalName,
                    patch.XPath!))
                .ToArray();
        }
        catch
        {
            return [];
        }
    }

    private static IReadOnlyList<ModConflict> FindModConflicts(
        ChangedFile changedFile,
        Dictionary<string, List<ModPatchOperation>> modPatchIndex,
        VersionComparison comparison,
        Dictionary<string, XmlChangedNodeIndex> changedNodeIndexes,
        CancellationToken cancellationToken)
    {
        if (modPatchIndex.Count == 0 || !changedFile.IsXmlText)
        {
            return [];
        }

        var targetPath = NormalizeTargetPath(changedFile.RelativePath);
        if (!modPatchIndex.TryGetValue(targetPath, out var patches))
        {
            return [];
        }

        var changedNodeIndex = GetChangedNodeIndex(
            changedFile,
            comparison,
            changedNodeIndexes,
            cancellationToken);

        return patches
            .Where(patch => DoesPatchOverlapChangedNodes(patch, changedNodeIndex, cancellationToken))
            .Select(patch => new ModConflict(
                patch.ModName,
                patch.ModRelativePath,
                patch.Operation,
                patch.XPath))
            .ToArray();
    }

    private static XmlChangedNodeIndex GetChangedNodeIndex(
        ChangedFile changedFile,
        VersionComparison comparison,
        Dictionary<string, XmlChangedNodeIndex> changedNodeIndexes,
        CancellationToken cancellationToken)
    {
        var targetPath = NormalizeTargetPath(changedFile.RelativePath);
        if (changedNodeIndexes.TryGetValue(targetPath, out var changedNodeIndex))
        {
            return changedNodeIndex;
        }

        changedNodeIndex = BuildChangedNodeIndex(changedFile, comparison, cancellationToken);
        changedNodeIndexes[targetPath] = changedNodeIndex;
        return changedNodeIndex;
    }

    private static XmlChangedNodeIndex BuildChangedNodeIndex(
        ChangedFile changedFile,
        VersionComparison comparison,
        CancellationToken cancellationToken)
    {
        if (changedFile.ChangeType is FileChangeType.Added or FileChangeType.Removed
            || string.IsNullOrWhiteSpace(comparison.StartDirectory)
            || string.IsNullOrWhiteSpace(comparison.EndDirectory))
        {
            return XmlChangedNodeIndex.WholeFile;
        }

        var startPath = Path.Combine(
            comparison.StartDirectory,
            changedFile.RelativePath.Replace('/', Path.DirectorySeparatorChar));
        var endPath = Path.Combine(
            comparison.EndDirectory,
            changedFile.RelativePath.Replace('/', Path.DirectorySeparatorChar));

        if (!File.Exists(startPath) || !File.Exists(endPath))
        {
            return XmlChangedNodeIndex.WholeFile;
        }

        try
        {
            var startDocument = XDocument.Load(startPath, LoadOptions.PreserveWhitespace);
            cancellationToken.ThrowIfCancellationRequested();
            var endDocument = XDocument.Load(endPath, LoadOptions.PreserveWhitespace);
            cancellationToken.ThrowIfCancellationRequested();

            var startFingerprints = BuildXmlNodeFingerprints(startDocument);
            var endFingerprints = BuildXmlNodeFingerprints(endDocument);
            var changedPaths = startFingerprints.Keys
                .Union(endFingerprints.Keys, StringComparer.Ordinal)
                .Where(path => !startFingerprints.TryGetValue(path, out var startFingerprint)
                    || !endFingerprints.TryGetValue(path, out var endFingerprint)
                    || !string.Equals(startFingerprint, endFingerprint, StringComparison.Ordinal))
                .ToHashSet(StringComparer.Ordinal);

            return new XmlChangedNodeIndex(false, startDocument, endDocument, changedPaths);
        }
        catch
        {
            return XmlChangedNodeIndex.WholeFile;
        }
    }

    private static Dictionary<string, string> BuildXmlNodeFingerprints(XDocument document)
    {
        var fingerprints = new Dictionary<string, string>(StringComparer.Ordinal);
        if (document.Root is null)
        {
            return fingerprints;
        }

        foreach (var element in document.Root.DescendantsAndSelf())
        {
            var elementPath = BuildElementPath(element);
            fingerprints[elementPath] = BuildElementFingerprint(element);

            foreach (var attribute in element.Attributes())
            {
                if (attribute.IsNamespaceDeclaration)
                {
                    continue;
                }

                fingerprints[$"{elementPath}/@{attribute.Name.LocalName}"] = attribute.Value;
            }
        }

        return fingerprints;
    }

    private static string BuildElementFingerprint(XElement element)
    {
        var directText = string.Concat(element.Nodes().OfType<XText>().Select(text => text.Value));
        return $"{element.Name.LocalName}|{directText.Trim()}";
    }

    private static bool DoesPatchOverlapChangedNodes(
        ModPatchOperation patch,
        XmlChangedNodeIndex changedNodeIndex,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (changedNodeIndex.IsWholeFileChange)
        {
            return true;
        }

        if (changedNodeIndex.ChangedPaths.Count == 0)
        {
            return false;
        }

        var targetPaths = EvaluatePatchTargetPaths(patch.XPath, changedNodeIndex.StartDocument)
            .Concat(EvaluatePatchTargetPaths(patch.XPath, changedNodeIndex.EndDocument))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (targetPaths.Length == 0)
        {
            return false;
        }

        return targetPaths.Any(targetPath => changedNodeIndex.ChangedPaths.Any(
            changedPath => AreRelatedXmlPaths(targetPath, changedPath)));
    }

    private static IEnumerable<string> EvaluatePatchTargetPaths(string xpath, XDocument? document)
    {
        if (document is null)
        {
            return [];
        }

        try
        {
            var result = document.XPathEvaluate(xpath);
            if (result is string or double or bool || result is not IEnumerable nodes)
            {
                return [];
            }

            var paths = new List<string>();
            foreach (var node in nodes)
            {
                switch (node)
                {
                    case XElement element:
                        paths.Add(BuildElementPath(element));
                        break;
                    case XAttribute attribute:
                        paths.Add($"{BuildElementPath(attribute.Parent!)}/@{attribute.Name.LocalName}");
                        break;
                }
            }

            return paths;
        }
        catch (XPathException)
        {
            return [];
        }
        catch (XmlException)
        {
            return [];
        }
    }

    private static bool AreRelatedXmlPaths(string left, string right)
    {
        return string.Equals(left, right, StringComparison.Ordinal)
            || left.StartsWith($"{right}/", StringComparison.Ordinal)
            || right.StartsWith($"{left}/", StringComparison.Ordinal);
    }

    private static string BuildElementPath(XElement element)
    {
        var ancestors = element.Ancestors()
            .Reverse()
            .Append(element)
            .Select(BuildElementPathSegment);

        return "/" + string.Join("/", ancestors);
    }

    private static string BuildElementPathSegment(XElement element)
    {
        var identifier = GetElementIdentifier(element);
        if (!string.IsNullOrWhiteSpace(identifier))
        {
            return $"{element.Name.LocalName}{identifier}";
        }

        var sameNameSiblings = element.Parent?.Elements(element.Name).ToArray();
        if (sameNameSiblings is null || sameNameSiblings.Length <= 1)
        {
            return element.Name.LocalName;
        }

        var index = Array.IndexOf(sameNameSiblings, element) + 1;
        return $"{element.Name.LocalName}[{index.ToString(CultureInfo.InvariantCulture)}]";
    }

    private static string? GetElementIdentifier(XElement element)
    {
        foreach (var attributeName in new[] { "name", "id", "class" })
        {
            var attribute = element.Attribute(attributeName);
            if (attribute is not null && !string.IsNullOrWhiteSpace(attribute.Value))
            {
                return $"[@{attributeName}='{attribute.Value}']";
            }
        }

        return null;
    }

    private static IReadOnlyList<DiffLine> BuildModifiedFileDiff(
        string oldText,
        string newText,
        bool ignoreWhitespaceChanges)
    {
        var model = SideBySideDiffBuilder.Diff(oldText, newText, ignoreWhitespaceChanges, false);
        var oldLines = model.OldText.Lines;
        var newLines = model.NewText.Lines;
        var count = Math.Max(oldLines.Count, newLines.Count);
        var lines = new List<DiffLine>(count);

        for (var index = 0; index < count; index++)
        {
            var oldPiece = index < oldLines.Count ? oldLines[index] : null;
            var newPiece = index < newLines.Count ? newLines[index] : null;

            if (IsContextLine(oldPiece, newPiece))
            {
                lines.Add(new DiffLine(
                    oldPiece?.Position,
                    newPiece?.Position,
                    DiffLineKind.Context,
                    newPiece?.Text ?? oldPiece?.Text ?? string.Empty));
                continue;
            }

            if (ignoreWhitespaceChanges
                && oldPiece is not null
                && newPiece is not null
                && AreEquivalentIgnoringWhitespace(oldPiece.Text, newPiece.Text))
            {
                lines.Add(new DiffLine(
                    oldPiece.Position,
                    newPiece.Position,
                    DiffLineKind.Context,
                    newPiece.Text ?? oldPiece.Text ?? string.Empty));
                continue;
            }

            if (oldPiece is not null && IsRemovedLine(oldPiece.Type))
            {
                lines.Add(new DiffLine(
                    oldPiece.Position,
                    null,
                    DiffLineKind.Removed,
                    oldPiece.Text ?? string.Empty));
            }

            if (newPiece is not null && IsAddedLine(newPiece.Type))
            {
                lines.Add(new DiffLine(
                    null,
                    newPiece.Position,
                    DiffLineKind.Added,
                    newPiece.Text ?? string.Empty));
            }
        }

        return ignoreWhitespaceChanges
            ? NormalizeWhitespaceIgnoredDisplayLines(HideWhitespaceOnlyDiffLines(lines))
            : lines;
    }

    private static IReadOnlyList<DiffLine> NormalizeWhitespaceIgnoredDisplayLines(IReadOnlyList<DiffLine> lines)
    {
        return lines
            .Select(line => line with
            {
                Text = NormalizeWhitespaceIgnoredDisplayText(line.Text)
            })
            .ToArray();
    }

    private static IReadOnlyList<DiffLine> HideWhitespaceOnlyDiffLines(IReadOnlyList<DiffLine> lines)
    {
        var nonBlankDiffLines = lines
            .Where(ShouldKeepWhitespaceIgnoredDiffLine)
            .ToArray();
        var filteredLines = new List<DiffLine>(nonBlankDiffLines.Length);

        for (var index = 0; index < nonBlankDiffLines.Length;)
        {
            var line = nonBlankDiffLines[index];
            if (line.Kind == DiffLineKind.Context)
            {
                filteredLines.Add(line);
                index++;
                continue;
            }

            var blockStart = index;
            while (index < nonBlankDiffLines.Length
                && nonBlankDiffLines[index].Kind != DiffLineKind.Context)
            {
                index++;
            }

            filteredLines.AddRange(CollapseWhitespaceEquivalentDiffBlock(
                nonBlankDiffLines[blockStart..index]));
        }

        return filteredLines;
    }

    private static IReadOnlyList<DiffLine> CollapseWhitespaceEquivalentDiffBlock(IReadOnlyList<DiffLine> block)
    {
        var removedLines = block
            .Where(line => line.Kind == DiffLineKind.Removed)
            .ToArray();
        var addedLines = block
            .Where(line => line.Kind == DiffLineKind.Added)
            .ToArray();

        if (removedLines.Length == 0 || addedLines.Length == 0)
        {
            return block;
        }

        var matches = FindWhitespaceEquivalentLineMatches(removedLines, addedLines);
        if (matches.Count == 0)
        {
            return block;
        }

        var collapsedLines = new List<DiffLine>(block.Count);
        var removedIndex = 0;
        var addedIndex = 0;

        foreach (var match in matches)
        {
            while (removedIndex < match.OldIndex)
            {
                collapsedLines.Add(removedLines[removedIndex]);
                removedIndex++;
            }

            while (addedIndex < match.NewIndex)
            {
                collapsedLines.Add(addedLines[addedIndex]);
                addedIndex++;
            }

            collapsedLines.Add(new DiffLine(
                removedLines[match.OldIndex].OldLineNumber,
                addedLines[match.NewIndex].NewLineNumber,
                DiffLineKind.Context,
                addedLines[match.NewIndex].Text));
            removedIndex = match.OldIndex + 1;
            addedIndex = match.NewIndex + 1;
        }

        while (removedIndex < removedLines.Length)
        {
            collapsedLines.Add(removedLines[removedIndex]);
            removedIndex++;
        }

        while (addedIndex < addedLines.Length)
        {
            collapsedLines.Add(addedLines[addedIndex]);
            addedIndex++;
        }

        return collapsedLines;
    }

    private static IReadOnlyList<LineMatch> FindWhitespaceEquivalentLineMatches(
        IReadOnlyList<DiffLine> removedLines,
        IReadOnlyList<DiffLine> addedLines)
    {
        var removedKeys = removedLines
            .Select(line => NormalizeWhitespaceInsensitiveLine(line.Text))
            .ToArray();
        var addedKeys = addedLines
            .Select(line => NormalizeWhitespaceInsensitiveLine(line.Text))
            .ToArray();

        const int maxDynamicProgrammingCells = 1_000_000;
        return (long)removedKeys.Length * addedKeys.Length <= maxDynamicProgrammingCells
            ? FindWhitespaceEquivalentLineMatchesWithLcs(removedKeys, addedKeys)
            : FindWhitespaceEquivalentLineMatchesGreedy(removedKeys, addedKeys);
    }

    private static IReadOnlyList<LineMatch> FindWhitespaceEquivalentLineMatchesWithLcs(
        IReadOnlyList<string> removedKeys,
        IReadOnlyList<string> addedKeys)
    {
        var lengths = new int[removedKeys.Count + 1, addedKeys.Count + 1];

        for (var oldIndex = removedKeys.Count - 1; oldIndex >= 0; oldIndex--)
        {
            for (var newIndex = addedKeys.Count - 1; newIndex >= 0; newIndex--)
            {
                lengths[oldIndex, newIndex] = IsWhitespaceEquivalentKeyMatch(
                    removedKeys[oldIndex],
                    addedKeys[newIndex])
                    ? lengths[oldIndex + 1, newIndex + 1] + 1
                    : Math.Max(
                        lengths[oldIndex + 1, newIndex],
                        lengths[oldIndex, newIndex + 1]);
            }
        }

        var matches = new List<LineMatch>();
        var oldCursor = 0;
        var newCursor = 0;
        while (oldCursor < removedKeys.Count && newCursor < addedKeys.Count)
        {
            if (IsWhitespaceEquivalentKeyMatch(removedKeys[oldCursor], addedKeys[newCursor]))
            {
                matches.Add(new LineMatch(oldCursor, newCursor));
                oldCursor++;
                newCursor++;
                continue;
            }

            if (lengths[oldCursor + 1, newCursor] >= lengths[oldCursor, newCursor + 1])
            {
                oldCursor++;
            }
            else
            {
                newCursor++;
            }
        }

        return matches;
    }

    private static IReadOnlyList<LineMatch> FindWhitespaceEquivalentLineMatchesGreedy(
        IReadOnlyList<string> removedKeys,
        IReadOnlyList<string> addedKeys)
    {
        var addedIndexesByKey = new Dictionary<string, Queue<int>>(StringComparer.Ordinal);
        for (var index = 0; index < addedKeys.Count; index++)
        {
            var key = addedKeys[index];
            if (string.IsNullOrEmpty(key))
            {
                continue;
            }

            if (!addedIndexesByKey.TryGetValue(key, out var indexes))
            {
                indexes = new Queue<int>();
                addedIndexesByKey[key] = indexes;
            }

            indexes.Enqueue(index);
        }

        var matches = new List<LineMatch>();
        var lastAddedIndex = -1;

        for (var removedIndex = 0; removedIndex < removedKeys.Count; removedIndex++)
        {
            var key = removedKeys[removedIndex];
            if (string.IsNullOrEmpty(key)
                || !addedIndexesByKey.TryGetValue(key, out var addedIndexes))
            {
                continue;
            }

            while (addedIndexes.Count > 0 && addedIndexes.Peek() <= lastAddedIndex)
            {
                addedIndexes.Dequeue();
            }

            if (addedIndexes.Count == 0)
            {
                continue;
            }

            lastAddedIndex = addedIndexes.Dequeue();
            matches.Add(new LineMatch(removedIndex, lastAddedIndex));
        }

        return matches;
    }

    private static bool IsWhitespaceEquivalentKeyMatch(string oldKey, string newKey)
    {
        return !string.IsNullOrEmpty(oldKey)
            && string.Equals(oldKey, newKey, StringComparison.Ordinal);
    }

    private static bool ShouldKeepWhitespaceIgnoredDiffLine(DiffLine line)
    {
        return line.Kind is not (DiffLineKind.Added or DiffLineKind.Removed)
            || !string.IsNullOrWhiteSpace(line.Text);
    }

    private static bool AreEquivalentIgnoringWhitespace(string? oldText, string? newText)
    {
        if (oldText is null || newText is null)
        {
            return false;
        }

        var oldIndex = 0;
        var newIndex = 0;

        while (true)
        {
            oldIndex = MoveToNextNonWhitespace(oldText, oldIndex);
            newIndex = MoveToNextNonWhitespace(newText, newIndex);

            var oldEnded = oldIndex >= oldText.Length;
            var newEnded = newIndex >= newText.Length;
            if (oldEnded || newEnded)
            {
                return oldEnded && newEnded;
            }

            if (oldText[oldIndex] != newText[newIndex])
            {
                return false;
            }

            oldIndex++;
            newIndex++;
        }
    }

    private static int MoveToNextNonWhitespace(string text, int index)
    {
        while (index < text.Length && char.IsWhiteSpace(text[index]))
        {
            index++;
        }

        return index;
    }

    private static string NormalizeWhitespaceInsensitiveLine(string text)
    {
        var builder = new StringBuilder(text.Length);

        foreach (var character in text)
        {
            if (!char.IsWhiteSpace(character))
            {
                builder.Append(character);
            }
        }

        return builder.ToString();
    }

    private static string NormalizeWhitespaceIgnoredDisplayText(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        var trimmedEnd = text.TrimEnd();
        if (!trimmedEnd.EndsWith("/>", StringComparison.Ordinal))
        {
            return trimmedEnd;
        }

        var slashIndex = trimmedEnd.Length - 2;
        var lastContentIndex = slashIndex - 1;
        while (lastContentIndex >= 0 && char.IsWhiteSpace(trimmedEnd[lastContentIndex]))
        {
            lastContentIndex--;
        }

        return lastContentIndex == slashIndex - 1
            ? trimmedEnd
            : $"{trimmedEnd[..(lastContentIndex + 1)]}/>";
    }

    private static bool IsContextLine(DiffPiece? oldPiece, DiffPiece? newPiece)
    {
        return oldPiece?.Type == ChangeType.Unchanged || newPiece?.Type == ChangeType.Unchanged;
    }

    private static bool IsRemovedLine(ChangeType type)
    {
        return type is ChangeType.Deleted or ChangeType.Modified;
    }

    private static bool IsAddedLine(ChangeType type)
    {
        return type is ChangeType.Inserted or ChangeType.Modified;
    }

    private static string NormalizeRelativePath(string path)
    {
        return path.Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/');
    }

    private static FileComparisonKind GetComparisonKind(string relativePath)
    {
        return IsItemIconsPath(relativePath)
            ? FileComparisonKind.BinaryAsset
            : FileComparisonKind.XmlText;
    }

    private static bool IsItemIconsPath(string relativePath)
    {
        var segments = NormalizeRelativePath(relativePath)
            .Split('/', StringSplitOptions.RemoveEmptyEntries);

        return segments.Any(segment => string.Equals(segment, "ItemIcons", StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizeTargetPath(string path)
    {
        var normalized = NormalizeRelativePath(path).TrimStart('/');
        return normalized.StartsWith("Data/", StringComparison.OrdinalIgnoreCase)
            ? normalized["Data/".Length..]
            : normalized;
    }

    private static string NormalizeDirectoryForCacheKey(string directory)
    {
        return NormalizeRelativePath(Path.GetFullPath(directory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
    }

    private static void AppendHashData(IncrementalHash hash, string value)
    {
        hash.AppendData(Encoding.UTF8.GetBytes(value));
        hash.AppendData(new byte[] { 0 });
    }

    private static bool FilesHaveSameContent(string leftPath, string rightPath)
    {
        var leftInfo = new FileInfo(leftPath);
        var rightInfo = new FileInfo(rightPath);
        if (leftInfo.Length != rightInfo.Length)
        {
            return false;
        }

        using var left = File.OpenRead(leftPath);
        using var right = File.OpenRead(rightPath);
        return left.Length == right.Length
            && CryptographicOperations.FixedTimeEquals(
                SHA256.HashData(left),
                SHA256.HashData(right));
    }

    private static string FormatFileSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        var size = (double)bytes;
        var unitIndex = 0;

        while (size >= 1024 && unitIndex < units.Length - 1)
        {
            size /= 1024;
            unitIndex++;
        }

        return unitIndex == 0
            ? $"{bytes} {units[unitIndex]}"
            : $"{size:0.##} {units[unitIndex]}";
    }

    private static IReadOnlyList<string> SplitLines(string text)
    {
        using var reader = new StringReader(text);
        var lines = new List<string>();

        while (reader.ReadLine() is { } line)
        {
            lines.Add(line);
        }

        return lines;
    }

    private sealed record XmlFileCatalog(
        SortedDictionary<string, string> Files,
        FolderFingerprint Fingerprint);

    private sealed record XmlFileEntry(
        string FullPath,
        long Length,
        long LastWriteUtcTicks);

    private sealed record ModPatchOperation(
        string ModName,
        string ModRelativePath,
        string TargetPath,
        string Operation,
        string XPath);

    private sealed record LineMatch(
        int OldIndex,
        int NewIndex);

    private sealed record XmlChangedNodeIndex(
        bool IsWholeFileChange,
        XDocument? StartDocument,
        XDocument? EndDocument,
        IReadOnlySet<string> ChangedPaths)
    {
        public static XmlChangedNodeIndex WholeFile { get; } = new(true, null, null, new HashSet<string>());
    }
}
