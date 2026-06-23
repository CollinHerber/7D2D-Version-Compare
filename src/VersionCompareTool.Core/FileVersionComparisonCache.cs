using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VersionCompareTool.Core;

public sealed class FileVersionComparisonCache : IVersionComparisonCache
{
    private const int SchemaVersion = 3;
    private readonly string _cacheDirectory;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.General)
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() }
    };

    public FileVersionComparisonCache(string cacheDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheDirectory);
        _cacheDirectory = cacheDirectory;
    }

    public static FileVersionComparisonCache CreateDefault()
    {
        var localApplicationData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var root = string.IsNullOrWhiteSpace(localApplicationData)
            ? Path.Combine(AppContext.BaseDirectory, ".cache")
            : localApplicationData;

        return new FileVersionComparisonCache(
            Path.Combine(root, "7D2D-Version-Compare", "DiffCache"));
    }

    public VersionComparison? TryLoad(
        VersionComparisonCacheKey key,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            var cachePath = GetCachePath(key);
            if (!File.Exists(cachePath))
            {
                return null;
            }

            using var stream = File.OpenRead(cachePath);
            var envelope = JsonSerializer.Deserialize<CacheEnvelope>(stream, JsonOptions);
            cancellationToken.ThrowIfCancellationRequested();

            if (envelope is null
                || envelope.SchemaVersion != SchemaVersion
                || envelope.Key != key)
            {
                return null;
            }

            return ToComparison(envelope.Comparison) with
            {
                IsFromCache = true
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    public void Save(
        VersionComparisonCacheKey key,
        VersionComparison comparison,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            Directory.CreateDirectory(_cacheDirectory);

            var cachePath = GetCachePath(key);
            var tempPath = $"{cachePath}.{Guid.NewGuid():N}.tmp";
            var envelope = new CacheEnvelope(
                SchemaVersion,
                key,
                ToCachedComparison(comparison),
                DateTimeOffset.UtcNow);

            using (var stream = File.Create(tempPath))
            {
                JsonSerializer.Serialize(stream, envelope, JsonOptions);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(tempPath, cachePath, overwrite: true);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
        }
    }

    private string GetCachePath(VersionComparisonCacheKey key)
    {
        var keyJson = JsonSerializer.Serialize(key, JsonOptions);
        var keyHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(keyJson))).ToLowerInvariant();
        return Path.Combine(_cacheDirectory, $"{keyHash}.json");
    }

    private static CachedVersionComparison ToCachedComparison(VersionComparison comparison)
    {
        return new CachedVersionComparison(
            comparison.StartVersion,
            comparison.EndVersion,
            comparison.ChangedFiles
                .Select(file => new CachedChangedFile(
                    file.RelativePath,
                    file.ChangeType,
                    file.ComparisonKind,
                    file.Additions,
                    file.Deletions,
                    file.Lines
                        .Select(line => new CachedDiffLine(
                            line.OldLineNumber,
                            line.NewLineNumber,
                            line.Kind,
                            line.Text))
                        .ToArray()))
                .ToArray());
    }

    private static VersionComparison ToComparison(CachedVersionComparison comparison)
    {
        return new VersionComparison(
            comparison.StartVersion,
            comparison.EndVersion,
            comparison.ChangedFiles
                .Select(file => new ChangedFile(
                    file.RelativePath,
                    file.ChangeType,
                    file.Additions,
                    file.Deletions,
                    file.Lines
                        .Select(line => new DiffLine(
                            line.OldLineNumber,
                            line.NewLineNumber,
                            line.Kind,
                            line.Text))
                        .ToArray(),
                    file.ComparisonKind))
                .ToArray());
    }

    private sealed record CacheEnvelope(
        int SchemaVersion,
        VersionComparisonCacheKey Key,
        CachedVersionComparison Comparison,
        DateTimeOffset CreatedAtUtc);

    private sealed record CachedVersionComparison(
        string StartVersion,
        string EndVersion,
        CachedChangedFile[] ChangedFiles);

    private sealed record CachedChangedFile(
        string RelativePath,
        FileChangeType ChangeType,
        FileComparisonKind ComparisonKind,
        int Additions,
        int Deletions,
        CachedDiffLine[] Lines);

    private sealed record CachedDiffLine(
        int? OldLineNumber,
        int? NewLineNumber,
        DiffLineKind Kind,
        string Text);
}
