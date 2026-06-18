namespace VersionCompareTool.Core;

public sealed record DiffLine(
    int? OldLineNumber,
    int? NewLineNumber,
    DiffLineKind Kind,
    string Text);
