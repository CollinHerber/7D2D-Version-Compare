namespace VersionCompareTool.Core;

public sealed record FolderFingerprint(
    int FileCount,
    long TotalBytes,
    long LatestWriteUtcTicks,
    string MetadataHash);
