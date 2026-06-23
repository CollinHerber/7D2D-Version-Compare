using Avalonia.Media;

namespace VersionCompareTool.ViewModels;

public sealed class SideBySideDiffRowViewModel
{
    public SideBySideDiffRowViewModel(DiffLineViewModel? oldLine, DiffLineViewModel? newLine)
    {
        OldLine = oldLine;
        NewLine = newLine;
    }

    public DiffLineViewModel? OldLine { get; }

    public DiffLineViewModel? NewLine { get; }

    public DiffLineViewModel? PrimaryLine => NewLine?.IsDiff == true ? NewLine : OldLine ?? NewLine;

    public string OldLineNumber => OldLine?.OldLineNumber ?? string.Empty;

    public string NewLineNumber => NewLine?.NewLineNumber ?? string.Empty;

    public string OldText => OldLine?.Text ?? string.Empty;

    public string NewText => NewLine?.Text ?? string.Empty;

    public IBrush OldBackground => OldLine?.Background ?? EmptyBackground;

    public IBrush NewBackground => NewLine?.Background ?? EmptyBackground;

    public IBrush OldForeground => OldLine?.Foreground ?? EmptyForeground;

    public IBrush NewForeground => NewLine?.Foreground ?? EmptyForeground;

    public IBrush OldMutedForeground => OldLine?.MutedForeground ?? EmptyForeground;

    public IBrush NewMutedForeground => NewLine?.MutedForeground ?? EmptyForeground;

    public IBrush OldBorderBrush => OldLine?.BorderBrush ?? EmptyBorder;

    public IBrush NewBorderBrush => NewLine?.BorderBrush ?? EmptyBorder;

    private static IBrush EmptyBackground => Brush.Parse("#101418");

    private static IBrush EmptyForeground => Brush.Parse("#4D5A63");

    private static IBrush EmptyBorder => Brush.Parse("#172129");
}
