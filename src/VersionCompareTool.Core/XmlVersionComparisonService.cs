using DiffPlex.DiffBuilder;
using DiffPlex.DiffBuilder.Model;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

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

        var startCatalog = LoadXmlFiles(startDirectory, cancellationToken);
        var endCatalog = LoadXmlFiles(endDirectory, cancellationToken);
        var cacheKey = new VersionComparisonCacheKey(
            startVersion,
            endVersion,
            NormalizeDirectoryForCacheKey(startDirectory),
            NormalizeDirectoryForCacheKey(endDirectory),
            startCatalog.Fingerprint,
            endCatalog.Fingerprint,
            ignoreWhitespaceChanges);

        var cachedComparison = _cache.TryLoad(cacheKey, cancellationToken);
        if (cachedComparison is not null)
        {
            return ApplyModConflicts(cachedComparison, modName, modDirectory, cancellationToken);
        }

        var comparison = BuildComparison(
            startVersion,
            endVersion,
            startCatalog.Files,
            endCatalog.Files,
            ignoreWhitespaceChanges,
            cancellationToken);

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

        var modConflictIndex = LoadModConflictIndex(modName, modDirectory, cancellationToken);
        var changedFiles = comparison.ChangedFiles
            .Select(file =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return file.WithModConflicts(FindModConflicts(file.RelativePath, modConflictIndex));
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

    private static Dictionary<string, List<ModConflict>> LoadModConflictIndex(
        string? modName,
        string? modDirectory,
        CancellationToken cancellationToken)
    {
        var index = new Dictionary<string, List<ModConflict>>(PathComparer);

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

            if (!index.TryGetValue(targetPath, out var conflicts))
            {
                conflicts = [];
                index[targetPath] = conflicts;
            }

            conflicts.Add(new ModConflict(modName, relativePath));
        }

        return index;
    }

    private static IReadOnlyList<ModConflict> FindModConflicts(
        string changedFilePath,
        Dictionary<string, List<ModConflict>> modConflictIndex)
    {
        if (modConflictIndex.Count == 0)
        {
            return [];
        }

        var targetPath = NormalizeTargetPath(changedFilePath);
        return modConflictIndex.TryGetValue(targetPath, out var conflicts)
            ? conflicts.ToArray()
            : [];
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

        return lines;
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
}
