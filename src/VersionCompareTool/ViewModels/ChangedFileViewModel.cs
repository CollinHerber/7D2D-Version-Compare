using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using VersionCompareTool.Core;

namespace VersionCompareTool.ViewModels;

public sealed class ChangedFileViewModel : ObservableObject
{
    private bool isSelected;

    public ChangedFileViewModel(ChangedFile model)
    {
        Model = model;
    }

    public ChangedFile Model { get; }

    public string RelativePath => Model.RelativePath;

    public string FileName => Path.GetFileName(Model.RelativePath);

    public string DirectoryPath
    {
        get
        {
            var directory = Path.GetDirectoryName(Model.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            return string.IsNullOrWhiteSpace(directory)
                ? "/"
                : directory.Replace(Path.DirectorySeparatorChar, '/');
        }
    }

    public string ChangeTypeText => Model.ChangeType.ToString();

    public string AdditionsText => Model.Additions > 0 ? $"+{Model.Additions}" : string.Empty;

    public string DeletionsText => Model.Deletions > 0 ? $"-{Model.Deletions}" : string.Empty;

    public bool IsSelected
    {
        get => isSelected;
        set
        {
            if (SetProperty(ref isSelected, value))
            {
                OnPropertyChanged(nameof(CardBackground));
                OnPropertyChanged(nameof(CardBorderBrush));
                OnPropertyChanged(nameof(SelectionAccentBrush));
            }
        }
    }

    public bool HasModConflicts => Model.HasModConflicts;

    public string ModConflictCountText => Model.ModConflicts.Count == 1
        ? "1 mod file"
        : $"{Model.ModConflicts.Count} mod files";

    public string ModConflictSummary => Model.ModConflicts.Count == 1
        ? $"Mod conflict: {Model.ModConflicts[0].ModRelativePath}"
        : $"Mod conflicts: {string.Join(", ", Model.ModConflicts.Select(conflict => conflict.ModRelativePath))}";

    public IBrush CardBackground => (IsSelected, HasModConflicts) switch
    {
        (true, true) => Brush.Parse("#3A2A1F"),
        (true, false) => Brush.Parse("#223241"),
        (false, true) => Brush.Parse("#251C17"),
        _ => Brush.Parse("#1B2329")
    };

    public IBrush CardBorderBrush => IsSelected
        ? Brush.Parse("#67C7FF")
        : HasModConflicts
            ? Brush.Parse("#B85C2E")
            : Brush.Parse("#2B3842");

    public IBrush SelectionAccentBrush => IsSelected
        ? Brush.Parse("#67C7FF")
        : Brush.Parse("#00000000");

    public IBrush BadgeBackground => Model.ChangeType switch
    {
        FileChangeType.Added => Brush.Parse("#1E6B3B"),
        FileChangeType.Removed => Brush.Parse("#7A2530"),
        _ => Brush.Parse("#6E5414")
    };

    public IBrush BadgeForeground => Brush.Parse("#FFF7E0");
}
