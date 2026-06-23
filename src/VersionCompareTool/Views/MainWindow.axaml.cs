using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace VersionCompareTool.Views;

public partial class MainWindow : Window
{
    private const double SideBySideWheelScrollAmount = 48;

    private ScrollViewer? _oldSideBySideScrollViewer;
    private ScrollViewer? _newSideBySideScrollViewer;
    private bool _isSideBySideScrollWired;
    private bool _isSyncingSideBySideScroll;

    public MainWindow()
    {
        InitializeComponent();
        DiffListBox.SelectionChanged += OnDiffListBoxSelectionChanged;
        OldSideBySideListBox.SelectionChanged += OnSideBySideListBoxSelectionChanged;
        NewSideBySideListBox.SelectionChanged += OnSideBySideListBoxSelectionChanged;
        OldSideBySideListBox.AddHandler(
            InputElement.PointerWheelChangedEvent,
            OnSideBySidePointerWheelChanged,
            RoutingStrategies.Tunnel,
            handledEventsToo: true);
        NewSideBySideListBox.AddHandler(
            InputElement.PointerWheelChangedEvent,
            OnSideBySidePointerWheelChanged,
            RoutingStrategies.Tunnel,
            handledEventsToo: true);
        OldSideBySideListBox.AttachedToVisualTree += (_, _) => QueueWireSideBySideScrollViewers();
        NewSideBySideListBox.AttachedToVisualTree += (_, _) => QueueWireSideBySideScrollViewers();
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

    private void OnSideBySideListBoxSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        var selectedItem = OldSideBySideListBox.SelectedItem ?? NewSideBySideListBox.SelectedItem;
        if (selectedItem is null)
        {
            return;
        }

        Dispatcher.UIThread.Post(
            () =>
            {
                OldSideBySideListBox.ScrollIntoView(selectedItem);
                NewSideBySideListBox.ScrollIntoView(selectedItem);
            },
            DispatcherPriority.Background);
    }

    private void QueueWireSideBySideScrollViewers()
    {
        Dispatcher.UIThread.Post(WireSideBySideScrollViewers, DispatcherPriority.Background);
    }

    private void WireSideBySideScrollViewers()
    {
        if (_isSideBySideScrollWired)
        {
            return;
        }

        var oldScrollViewer = OldSideBySideListBox.FindDescendantOfType<ScrollViewer>();
        var newScrollViewer = NewSideBySideListBox.FindDescendantOfType<ScrollViewer>();
        if (oldScrollViewer is null || newScrollViewer is null)
        {
            return;
        }

        _oldSideBySideScrollViewer = oldScrollViewer;
        _newSideBySideScrollViewer = newScrollViewer;
        _oldSideBySideScrollViewer.ScrollChanged += OnSideBySideScrollChanged;
        _newSideBySideScrollViewer.ScrollChanged += OnSideBySideScrollChanged;
        _isSideBySideScrollWired = true;
    }

    private void OnSideBySidePointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (_isSyncingSideBySideScroll || e.Delta.Y == 0)
        {
            return;
        }

        if (!TryGetSideBySideScrollViewers(
            out var oldScrollViewer,
            out var newScrollViewer))
        {
            return;
        }

        var source = ReferenceEquals(sender, OldSideBySideListBox)
            ? oldScrollViewer
            : newScrollViewer;
        var nextVerticalOffset = source.Offset.Y - e.Delta.Y * SideBySideWheelScrollAmount;

        SyncSideBySideVerticalOffset(nextVerticalOffset);
        e.Handled = true;
    }

    private void OnSideBySideScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (_isSyncingSideBySideScroll || e.OffsetDelta.Y == 0)
        {
            return;
        }

        var source = sender as ScrollViewer;
        if (source is null)
        {
            return;
        }

        SyncSideBySideVerticalOffset(source.Offset.Y);
    }

    private bool TryGetSideBySideScrollViewers(
        out ScrollViewer oldScrollViewer,
        out ScrollViewer newScrollViewer)
    {
        if (!_isSideBySideScrollWired)
        {
            WireSideBySideScrollViewers();
        }

        if (_oldSideBySideScrollViewer is { } oldViewer
            && _newSideBySideScrollViewer is { } newViewer)
        {
            oldScrollViewer = oldViewer;
            newScrollViewer = newViewer;
            return true;
        }

        oldScrollViewer = null!;
        newScrollViewer = null!;
        return false;
    }

    private void SyncSideBySideVerticalOffset(double verticalOffset)
    {
        if (!TryGetSideBySideScrollViewers(
            out var oldScrollViewer,
            out var newScrollViewer))
        {
            return;
        }

        _isSyncingSideBySideScroll = true;

        try
        {
            SetVerticalOffset(oldScrollViewer, verticalOffset);
            SetVerticalOffset(newScrollViewer, verticalOffset);
        }
        finally
        {
            _isSyncingSideBySideScroll = false;
        }
    }

    private static void SetVerticalOffset(ScrollViewer scrollViewer, double verticalOffset)
    {
        scrollViewer.Offset = new Vector(
            scrollViewer.Offset.X,
            ClampVerticalOffset(scrollViewer, verticalOffset));
    }

    private static double ClampVerticalOffset(ScrollViewer scrollViewer, double verticalOffset)
    {
        var maxOffset = Math.Max(0, scrollViewer.Extent.Height - scrollViewer.Viewport.Height);
        return Math.Clamp(verticalOffset, 0, maxOffset);
    }
}
