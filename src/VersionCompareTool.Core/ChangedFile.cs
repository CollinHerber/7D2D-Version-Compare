namespace VersionCompareTool.Core;

public sealed record ChangedFile
{
    public ChangedFile(
        string relativePath,
        FileChangeType changeType,
        int additions,
        int deletions,
        IReadOnlyList<DiffLine> lines,
        IReadOnlyList<ModConflict>? modConflicts = null)
    {
        RelativePath = relativePath;
        ChangeType = changeType;
        Additions = additions;
        Deletions = deletions;
        Lines = lines;
        ModConflicts = modConflicts ?? [];
    }

    public string RelativePath { get; }

    public FileChangeType ChangeType { get; }

    public int Additions { get; }

    public int Deletions { get; }

    public IReadOnlyList<DiffLine> Lines { get; }

    public IReadOnlyList<ModConflict> ModConflicts { get; }

    public int TotalChanges => Additions + Deletions;

    public bool HasModConflicts => ModConflicts.Count > 0;

    public ChangedFile WithModConflicts(IReadOnlyList<ModConflict> modConflicts)
    {
        return new ChangedFile(
            RelativePath,
            ChangeType,
            Additions,
            Deletions,
            Lines,
            modConflicts);
    }
}
