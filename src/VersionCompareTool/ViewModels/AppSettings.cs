namespace VersionCompareTool.ViewModels;

public sealed class AppSettings
{
    public string? SelectedStartVersion { get; init; }

    public string? SelectedEndVersion { get; init; }

    public string? SelectedModName { get; init; }

    public bool IsFolderView { get; init; }

    public bool IsSideBySideDiffView { get; init; } = true;

    public bool ShowOnlyModConflicts { get; init; }

    public bool IgnoreWhitespaceChanges { get; init; } = true;
}
