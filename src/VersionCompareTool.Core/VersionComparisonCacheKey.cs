namespace VersionCompareTool.Core;

public sealed record VersionComparisonCacheKey(
    string StartVersion,
    string EndVersion,
    string StartDirectory,
    string EndDirectory,
    FolderFingerprint StartFingerprint,
    FolderFingerprint EndFingerprint);
