namespace VersionCompareTool.Core;

public static class VersionComparisonCacheFactory
{
    public const string DisableCacheEnvironmentVariable = "VERSION_COMPARE_DISABLE_CACHE";

    public static IVersionComparisonCache CreateDefault()
    {
        return IsCacheDisabled()
            ? DisabledVersionComparisonCache.Instance
            : FileVersionComparisonCache.CreateDefault();
    }

    public static bool IsCacheDisabled()
    {
        var value = Environment.GetEnvironmentVariable(DisableCacheEnvironmentVariable);
        return string.Equals(value, "1", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase);
    }
}
