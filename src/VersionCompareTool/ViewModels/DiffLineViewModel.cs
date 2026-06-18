using System.Globalization;
using Avalonia.Media;
using VersionCompareTool.Core;

namespace VersionCompareTool.ViewModels;

public sealed class DiffLineViewModel
{
    public DiffLineViewModel(DiffLine model)
    {
        Model = model;
    }

    public DiffLine Model { get; }

    public bool IsDiff => Model.Kind is DiffLineKind.Added or DiffLineKind.Removed;

    public string OldLineNumber => Model.OldLineNumber?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;

    public string NewLineNumber => Model.NewLineNumber?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;

    public string Marker => Model.Kind switch
    {
        DiffLineKind.Added => "+",
        DiffLineKind.Removed => "-",
        _ => string.Empty
    };

    public string Text => Model.Text;

    public IBrush Background => Model.Kind switch
    {
        DiffLineKind.Added => Brush.Parse("#143322"),
        DiffLineKind.Removed => Brush.Parse("#3B171B"),
        _ => Brush.Parse("#11171B")
    };

    public IBrush Foreground => Model.Kind switch
    {
        DiffLineKind.Added => Brush.Parse("#DDFBE8"),
        DiffLineKind.Removed => Brush.Parse("#FFE0E0"),
        _ => Brush.Parse("#D0D7DE")
    };

    public IBrush MutedForeground => Brush.Parse("#7D8C96");

    public IBrush BorderBrush => Model.Kind switch
    {
        DiffLineKind.Added => Brush.Parse("#1F5A38"),
        DiffLineKind.Removed => Brush.Parse("#66313A"),
        _ => Brush.Parse("#172129")
    };
}
