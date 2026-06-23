namespace VersionCompareTool.Core;

public sealed record ModConflict(
    string ModName,
    string ModRelativePath,
    string? Operation = null,
    string? XPath = null);
