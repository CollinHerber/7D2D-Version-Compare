namespace VersionCompareTool.Core;

public sealed record DiffLine(
    int? OldLineNumber,
    int? NewLineNumber,
    DiffLineKind Kind,
    string Text)
{
    public string OldText { get; init; } = Text;

    public string NewText { get; init; } = Text;
}
