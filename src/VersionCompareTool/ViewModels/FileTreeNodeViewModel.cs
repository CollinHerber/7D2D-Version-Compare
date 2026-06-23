using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;

namespace VersionCompareTool.ViewModels;

public sealed class FileTreeNodeViewModel : ObservableObject
{
    private bool isExpanded;

    public FileTreeNodeViewModel(
        string name,
        FileTreeNodeViewModel? parent = null,
        ChangedFileViewModel? file = null)
    {
        Name = name;
        Parent = parent;
        File = file;

        if (File is not null)
        {
            File.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName is nameof(ChangedFileViewModel.CardBackground)
                    or nameof(ChangedFileViewModel.CardBorderBrush)
                    or nameof(ChangedFileViewModel.SelectionAccentBrush))
                {
                    OnPropertyChanged(args.PropertyName);
                }
            };
        }
    }

    public string Name { get; }

    public FileTreeNodeViewModel? Parent { get; }

    public ChangedFileViewModel? File { get; }

    public ObservableCollection<FileTreeNodeViewModel> Children { get; } = [];

    public bool IsFile => File is not null;

    public bool IsFolder => File is null;

    public string DisplayName => File?.FileName ?? Name;

    public string DirectoryPath => File?.DirectoryPath ?? string.Empty;

    public string ChangeTypeText => File?.ChangeTypeText ?? string.Empty;

    public string AdditionsText => File?.AdditionsText ?? string.Empty;

    public string DeletionsText => File?.DeletionsText ?? string.Empty;

    public bool HasModConflicts => File?.HasModConflicts ?? false;

    public bool IsBinaryAsset => File?.IsBinaryAsset ?? false;

    public string ModConflictCountText => File?.ModConflictCountText ?? string.Empty;

    public IBrush CardBackground => File?.CardBackground ?? Brush.Parse("#00000000");

    public IBrush CardBorderBrush => File?.CardBorderBrush ?? Brush.Parse("#00000000");

    public IBrush SelectionAccentBrush => File?.SelectionAccentBrush ?? Brush.Parse("#00000000");

    public IBrush BadgeBackground => File?.BadgeBackground ?? Brush.Parse("#00000000");

    public IBrush BadgeForeground => File?.BadgeForeground ?? Brush.Parse("#FFF7E0");

    public IBrush FileKindBadgeBackground => File?.FileKindBadgeBackground ?? Brush.Parse("#00000000");

    public bool IsExpanded
    {
        get => isExpanded;
        set => SetProperty(ref isExpanded, value);
    }

    public string FileCountText
    {
        get
        {
            var count = CountFiles();
            return count == 1 ? "1 file" : $"{count} files";
        }
    }

    private int CountFiles()
    {
        if (IsFile)
        {
            return 1;
        }

        return Children.Sum(child => child.CountFiles());
    }
}
