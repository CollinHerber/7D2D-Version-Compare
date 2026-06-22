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

    public XmlVersionComparisonService()
        : this(FileVersionComparisonCache.CreateDefault())
    {
    }

    public XmlVersionComparisonService(IVersionComparisonCache cache)
    {
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
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
        var startCatalog = LoadXmlFiles(startDirectoryPath, cancellationToken);
        var endCatalog = LoadXmlFiles(endDirectoryPath, cancellationToken);
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
            EndDirectory = endDirectoryPath
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

            if (!hasStartFile && hasEndFile && endPath is not null)
            {
                changedFiles.Add(BuildWholeFileChange(
                    relativePath,
                    endPath,
                    FileChangeType.Added));
                continue;
            }

            if (hasStartFile && startPath is not null && !hasEndFile)
            {
                changedFiles.Add(BuildWholeFileChange(
                    relativePath,
                    startPath,
                    FileChangeType.Removed));
                continue;
            }

            if (startPath is null || endPath is null)
            {
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

    private static XmlFileCatalog LoadXmlFiles(
        string rootDirectory,
        CancellationToken cancellationToken)
    {
        var files = new SortedDictionary<string, string>(PathComparer);
        var entries = new SortedDictionary<string, XmlFileEntry>(PathComparer);

        foreach (var filePath in Directory.EnumerateFiles(rootDirectory, "*.xml", SearchOption.AllDirectories))
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
        FileChangeType changeType)
    {
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
        if (modPatchIndex.Count == 0)
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
            ? lines.Where(ShouldKeepWhitespaceIgnoredDiffLine).ToArray()
            : lines;
    }

    private static bool ShouldKeepWhitespaceIgnoredDiffLine(DiffLine line)
    {
        return line.Kind is not (DiffLineKind.Added or DiffLineKind.Removed)
            || !string.IsNullOrWhiteSpace(line.Text);
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

    private sealed record XmlChangedNodeIndex(
        bool IsWholeFileChange,
        XDocument? StartDocument,
        XDocument? EndDocument,
        IReadOnlySet<string> ChangedPaths)
    {
        public static XmlChangedNodeIndex WholeFile { get; } = new(true, null, null, new HashSet<string>());
    }
}
