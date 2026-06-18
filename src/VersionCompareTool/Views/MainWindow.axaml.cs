using Avalonia.Controls;
using Avalonia.Threading;

namespace VersionCompareTool.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DiffListBox.SelectionChanged += OnDiffListBoxSelectionChanged;
    }

    private void OnDiffListBoxSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (DiffListBox.SelectedItem is null)
        {
            return;
        }

        Dispatcher.UIThread.Post(
            () => DiffListBox.ScrollIntoView(DiffListBox.SelectedItem),
            DispatcherPriority.Background);
    }
}
