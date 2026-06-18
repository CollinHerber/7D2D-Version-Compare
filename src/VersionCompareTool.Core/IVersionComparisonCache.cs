namespace VersionCompareTool.Core;

public interface IVersionComparisonCache
{
    VersionComparison? TryLoad(
        VersionComparisonCacheKey key,
        CancellationToken cancellationToken = default);

    void Save(
        VersionComparisonCacheKey key,
        VersionComparison comparison,
        CancellationToken cancellationToken = default);
}
