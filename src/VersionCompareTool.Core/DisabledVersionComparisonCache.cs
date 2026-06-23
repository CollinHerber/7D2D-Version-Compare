namespace VersionCompareTool.Core;

public sealed class DisabledVersionComparisonCache : IVersionComparisonCache
{
    public static DisabledVersionComparisonCache Instance { get; } = new();

    private DisabledVersionComparisonCache()
    {
    }

    public VersionComparison? TryLoad(
        VersionComparisonCacheKey key,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return null;
    }

    public void Save(
        VersionComparisonCacheKey key,
        VersionComparison comparison,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
    }
}
