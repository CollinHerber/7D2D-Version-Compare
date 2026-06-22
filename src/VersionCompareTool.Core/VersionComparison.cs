namespace VersionCompareTool.Core;

public sealed record VersionComparison(
    string StartVersion,
    string EndVersion,
    IReadOnlyList<ChangedFile> ChangedFiles,
    string? ModName = null,
    bool IsFromCache = false,
    string? StartDirectory = null,
    string? EndDirectory = null)
{
    public int AddedFiles => ChangedFiles.Count(file => file.ChangeType == FileChangeType.Added);

    public int ModifiedFiles => ChangedFiles.Count(file => file.ChangeType == FileChangeType.Modified);

    public int RemovedFiles => ChangedFiles.Count(file => file.ChangeType == FileChangeType.Removed);

    public int TotalAdditions => ChangedFiles.Sum(file => file.Additions);

    public int TotalDeletions => ChangedFiles.Sum(file => file.Deletions);

    public int ModConflictFiles => ChangedFiles.Count(file => file.HasModConflicts);

    public int TotalModConflicts => ChangedFiles.Sum(file => file.ModConflicts.Count);
}
