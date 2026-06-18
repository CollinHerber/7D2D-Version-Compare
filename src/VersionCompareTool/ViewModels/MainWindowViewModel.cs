using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VersionCompareTool.Core;

namespace VersionCompareTool.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    private readonly string _versionRoot;
    private readonly string _modRoot;
    private readonly XmlVersionComparisonService _comparisonService;
    private CancellationTokenSource? _versionComparisonCts;
    private CancellationTokenSource? _modConflictCts;
    private VersionComparison? _baseComparison;
    private IReadOnlyList<int> _diffAreaIndexes = [];
    private ChangedFileViewModel? _highlightedSelectedFile;
    private Dictionary<string, FileTreeNodeViewModel> _fileTreeNodesByPath = new(StringComparer.OrdinalIgnoreCase);
    private int _currentDiffAreaIndex = -1;
    private bool _isUpdatingSelections;
    private bool _isApplyingTreeSelection;
    private bool _isSyncingTreeSelection;

    [ObservableProperty]
    private string? selectedStartVersion;

    [ObservableProperty]
    private string? selectedEndVersion;

    [ObservableProperty]
    private string? selectedModName;

    [ObservableProperty]
    private ChangedFileViewModel? selectedFile;

    [ObservableProperty]
    private FileTreeNodeViewModel? selectedFileTreeNode;

    [ObservableProperty]
    private DiffLineViewModel? selectedDiffLine;

    [ObservableProperty]
    private bool isFolderView;

    [ObservableProperty]
    private string statusText = "Loading versions and mods...";

    [ObservableProperty]
    private string changedFileCountText = "0 changed files";

    [ObservableProperty]
    private string selectedFileTitle = "Loading versions and mods";

    [ObservableProperty]
    private string selectedFileSummary = "Scanning local Versions and Mods folders.";

    [ObservableProperty]
    private bool isBusy = true;

    [ObservableProperty]
    private string busyText = "Loading versions and mods...";

    [ObservableProperty]
    private ObservableCollection<ChangedFileViewModel> changedFiles = [];

    [ObservableProperty]
    private ObservableCollection<FileTreeNodeViewModel> fileTreeNodes = [];

    [ObservableProperty]
    private ObservableCollection<DiffLineViewModel> diffLines = [];

    [ObservableProperty]
    private string diffNavigationStatus = "0 / 0";

    public MainWindowViewModel()
        : this(
            ResolveWorkspaceFolder("Versions"),
            ResolveWorkspaceFolder("Mods"),
            new XmlVersionComparisonService())
    {
    }

    public MainWindowViewModel(
        string versionRoot,
        string modRoot,
        XmlVersionComparisonService comparisonService)
    {
        _versionRoot = versionRoot;
        _modRoot = modRoot;
        _comparisonService = comparisonService;

        _ = InitializeAsync();
    }

    public ObservableCollection<string> StartVersions { get; } = [];

    public ObservableCollection<string> EndVersions { get; } = [];

    public ObservableCollection<string> Mods { get; } = [];

    public bool HasSelectedFile => SelectedFile is not null;

    public bool HasNoSelectedFile => !HasSelectedFile;

    public bool IsNotBusy => !IsBusy;

    public bool IsFlatFileViewVisible => !IsFolderView;

    public bool IsFolderViewVisible => IsFolderView;

    partial void OnSelectedStartVersionChanged(string? value)
    {
        if (_isUpdatingSelections)
        {
            return;
        }

        _isUpdatingSelections = true;
        RefreshEndVersions();

        if (SelectedEndVersion is null
            || !EndVersions.Contains(SelectedEndVersion, StringComparer.OrdinalIgnoreCase))
        {
            SelectedEndVersion = EndVersions.FirstOrDefault();
        }

        _isUpdatingSelections = false;
        QueueVersionComparison();
    }

    partial void OnSelectedEndVersionChanged(string? value)
    {
        if (_isUpdatingSelections)
        {
            return;
        }

        QueueVersionComparison();
    }

    partial void OnSelectedModNameChanged(string? value)
    {
        if (_isUpdatingSelections)
        {
            return;
        }

        QueueModConflictScan();
    }

    partial void OnSelectedFileChanged(ChangedFileViewModel? value)
    {
        if (_highlightedSelectedFile is not null && !ReferenceEquals(_highlightedSelectedFile, value))
        {
            _highlightedSelectedFile.IsSelected = false;
        }

        if (value is not null)
        {
            value.IsSelected = true;
        }

        _highlightedSelectedFile = value;
        LoadSelectedFileDiff(value);

        if (!_isApplyingTreeSelection)
        {
            SyncSelectedFileTreeNode(value);
        }
    }

    partial void OnSelectedFileTreeNodeChanged(FileTreeNodeViewModel? value)
    {
        if (_isSyncingTreeSelection)
        {
            return;
        }

        _isApplyingTreeSelection = true;

        try
        {
            SelectedFile = value?.File;
        }
        finally
        {
            _isApplyingTreeSelection = false;
        }
    }

    partial void OnSelectedDiffLineChanged(DiffLineViewModel? value)
    {
        if (value is null)
        {
            return;
        }

        UpdateCurrentDiffAreaFromSelectedLine(value);
    }

    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(IsNotBusy));
    }

    partial void OnIsFolderViewChanged(bool value)
    {
        OnPropertyChanged(nameof(IsFlatFileViewVisible));
        OnPropertyChanged(nameof(IsFolderViewVisible));
        SyncSelectedFileTreeNode(SelectedFile);
    }

    private bool CanNavigateToPreviousDiffArea()
    {
        return _currentDiffAreaIndex > 0;
    }

    private bool CanNavigateToNextDiffArea()
    {
        return _currentDiffAreaIndex >= 0 && _currentDiffAreaIndex < _diffAreaIndexes.Count - 1;
    }

    [RelayCommand(CanExecute = nameof(CanNavigateToPreviousDiffArea))]
    private void NavigateToPreviousDiffArea()
    {
        if (!CanNavigateToPreviousDiffArea())
        {
            return;
        }

        SelectDiffArea(_currentDiffAreaIndex - 1);
    }

    [RelayCommand(CanExecute = nameof(CanNavigateToNextDiffArea))]
    private void NavigateToNextDiffArea()
    {
        if (!CanNavigateToNextDiffArea())
        {
            return;
        }

        SelectDiffArea(_currentDiffAreaIndex + 1);
    }

    private async Task InitializeAsync()
    {
        SetBusy("Loading versions and mods...");

        try
        {
            var folders = await Task.Run(() =>
            {
                Directory.CreateDirectory(_versionRoot);
                Directory.CreateDirectory(_modRoot);

                return new FolderLoadResult(
                    LoadFolders(_versionRoot),
                    LoadFolders(_modRoot));
            });

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                _isUpdatingSelections = true;

                ReplaceCollection(StartVersions, folders.Versions);
                ReplaceCollection(Mods, folders.Mods);

                SelectedStartVersion = folders.Versions.FirstOrDefault();
                RefreshEndVersions();
                SelectedEndVersion = EndVersions.FirstOrDefault();
                SelectedModName = null;

                _isUpdatingSelections = false;
                QueueVersionComparison();
            });
        }
        catch (Exception exception)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                CancelAllBackgroundWork();
                ClearCurrentResults();
                ChangedFileCountText = "0 changed files";
                StatusText = $"Failed to load versions and mods: {exception.Message}";
                SelectedFileTitle = "Failed to load versions and mods";
                SelectedFileSummary = exception.Message;
                ClearBusy();
                NotifySelectedFileStateChanged();
            });
        }
    }

    private static IReadOnlyList<string> LoadFolders(string root)
    {
        if (!Directory.Exists(root))
        {
            return [];
        }

        return Directory.EnumerateDirectories(root)
            .Select(Path.GetFileName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .Select(name => name!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private void QueueVersionComparison()
    {
        CancelAllBackgroundWork();
        _baseComparison = null;
        ClearCurrentResults();

        if (StartVersions.Count == 0)
        {
            ChangedFileCountText = "0 changed files";
            StatusText = $"No version folders found in {_versionRoot}.";
            SelectedFileTitle = "No version folders found";
            SelectedFileSummary = "Create folders like Versions/2.6 and Versions/3.0, then add XML files inside them.";
            ClearBusy();
            NotifySelectedFileStateChanged();
            return;
        }

        if (string.IsNullOrWhiteSpace(SelectedStartVersion) || string.IsNullOrWhiteSpace(SelectedEndVersion))
        {
            ChangedFileCountText = "0 changed files";
            StatusText = $"Add at least two version folders under {_versionRoot}.";
            SelectedFileTitle = "Choose two versions";
            SelectedFileSummary = "The selected start version is excluded from the end version dropdown.";
            ClearBusy();
            NotifySelectedFileStateChanged();
            return;
        }

        if (string.Equals(SelectedStartVersion, SelectedEndVersion, StringComparison.OrdinalIgnoreCase))
        {
            ChangedFileCountText = "0 changed files";
            StatusText = "Choose two different versions to compare.";
            SelectedFileTitle = "Choose two versions";
            SelectedFileSummary = "The start and end versions must be different.";
            ClearBusy();
            NotifySelectedFileStateChanged();
            return;
        }

        var request = new VersionComparisonRequest(
            SelectedStartVersion,
            SelectedEndVersion,
            Path.Combine(_versionRoot, SelectedStartVersion),
            Path.Combine(_versionRoot, SelectedEndVersion));

        var cts = new CancellationTokenSource();
        _versionComparisonCts = cts;

        ChangedFileCountText = "Loading changes...";
        SelectedFileTitle = "Comparing versions";
        SelectedFileSummary = "Checking the local diff cache. XML files will be scanned if the cache is missing or stale.";
        SetBusy($"Comparing {request.StartVersion} to {request.EndVersion}...");
        NotifySelectedFileStateChanged();

        _ = RunVersionComparisonAsync(request, cts);
    }

    private async Task RunVersionComparisonAsync(
        VersionComparisonRequest request,
        CancellationTokenSource cts)
    {
        try
        {
            var comparison = await Task.Run(
                () => _comparisonService.Compare(
                    request.StartVersion,
                    request.EndVersion,
                    request.StartDirectory,
                    request.EndDirectory,
                    cancellationToken: cts.Token),
                cts.Token);

            var changedFileViewModels = BuildChangedFileViewModels(comparison);

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (!ReferenceEquals(_versionComparisonCts, cts) || cts.IsCancellationRequested)
                {
                    return;
                }

                _baseComparison = comparison;
                _versionComparisonCts = null;

                if (!string.IsNullOrWhiteSpace(SelectedModName))
                {
                    QueueModConflictScan();
                    return;
                }

                ApplyComparison(comparison, changedFileViewModels);
                ClearBusy();
            });
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (!ReferenceEquals(_versionComparisonCts, cts))
                {
                    return;
                }

                _versionComparisonCts = null;
                ApplyComparisonError(exception);
                ClearBusy();
            });
        }
        finally
        {
            cts.Dispose();
        }
    }

    private void QueueModConflictScan()
    {
        CancelCurrentModConflictScan();

        if (_baseComparison is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(SelectedModName))
        {
            var comparison = _comparisonService.ApplyModConflicts(_baseComparison, null, null);
            ApplyComparison(comparison, BuildChangedFileViewModels(comparison));
            ClearBusy();
            return;
        }

        var request = new ModConflictRequest(
            SelectedModName,
            Path.Combine(_modRoot, SelectedModName));

        var cts = new CancellationTokenSource();
        _modConflictCts = cts;

        SetBusy($"Checking {request.ModName} for conflicts...");
        SelectedFileTitle = "Checking mod conflicts";
        SelectedFileSummary = $"Scanning {request.ModName} XML files and matching them against the current version diff.";
        NotifySelectedFileStateChanged();

        _ = RunModConflictScanAsync(_baseComparison, request, cts);
    }

    private async Task RunModConflictScanAsync(
        VersionComparison baseComparison,
        ModConflictRequest request,
        CancellationTokenSource cts)
    {
        try
        {
            var comparison = await Task.Run(
                () => _comparisonService.ApplyModConflicts(
                    baseComparison,
                    request.ModName,
                    request.ModDirectory,
                    cts.Token),
                cts.Token);

            var changedFileViewModels = BuildChangedFileViewModels(comparison);

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (!ReferenceEquals(_modConflictCts, cts) || cts.IsCancellationRequested)
                {
                    return;
                }

                ApplyComparison(comparison, changedFileViewModels);
                _modConflictCts = null;
                ClearBusy();
            });
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (!ReferenceEquals(_modConflictCts, cts))
                {
                    return;
                }

                ApplyComparisonError(exception);
                _modConflictCts = null;
                ClearBusy();
            });
        }
        finally
        {
            cts.Dispose();
        }
    }

    private void ApplyComparison(
        VersionComparison comparison,
        ObservableCollection<ChangedFileViewModel> changedFileViewModels)
    {
        ChangedFiles = changedFileViewModels;
        RebuildFileTree();
        DiffLines = [];
        SelectedFile = ChangedFiles.FirstOrDefault();

        ChangedFileCountText = ChangedFiles.Count == 1
            ? "1 changed file"
            : $"{ChangedFiles.Count} changed files";
        StatusText = BuildStatusText(comparison);

        if (SelectedFile is null)
        {
            SelectedFileTitle = "No XML changes found";
            SelectedFileSummary = "Choose another version range or add more XML snapshots.";
        }

        NotifySelectedFileStateChanged();
    }

    private void ApplyComparisonError(Exception exception)
    {
        ClearCurrentResults();
        ChangedFileCountText = "0 changed files";

        if (exception is DirectoryNotFoundException)
        {
            StatusText = exception.Message;
            SelectedFileTitle = "Version data not found";
            SelectedFileSummary = $"Add XML snapshots under {_versionRoot}\\<version> to compare real files.";
        }
        else
        {
            StatusText = $"Comparison failed: {exception.Message}";
            SelectedFileTitle = "Comparison failed";
            SelectedFileSummary = exception.Message;
        }

        NotifySelectedFileStateChanged();
    }

    private void LoadSelectedFileDiff(ChangedFileViewModel? file)
    {
        if (file is null)
        {
            DiffLines = [];
            SelectedDiffLine = null;
            ResetDiffNavigation();
            SelectedFileTitle = "Select a changed XML file";
            SelectedFileSummary = "The diff will appear here after you choose a file.";
            NotifySelectedFileStateChanged();
            return;
        }

        DiffLines = new ObservableCollection<DiffLineViewModel>(
            file.Model.Lines.Select(line => new DiffLineViewModel(line)));
        RebuildDiffAreaIndexes();

        SelectedFileTitle = file.RelativePath;
        SelectedFileSummary = BuildSelectedFileSummary(file);
        NotifySelectedFileStateChanged();
    }

    private void RefreshEndVersions()
    {
        var versions = StartVersions
            .Where(version => !string.Equals(version, SelectedStartVersion, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        ReplaceCollection(EndVersions, versions);
    }

    private static void ReplaceCollection<T>(ObservableCollection<T> collection, IEnumerable<T> values)
    {
        collection.Clear();

        foreach (var value in values)
        {
            collection.Add(value);
        }
    }

    private string? GetSelectedModDirectory()
    {
        return string.IsNullOrWhiteSpace(SelectedModName)
            ? null
            : Path.Combine(_modRoot, SelectedModName);
    }

    private static string ResolveWorkspaceFolder(string folderName)
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);

        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, folderName);
            if (Directory.Exists(candidate)
                || File.Exists(Path.Combine(current.FullName, "VersionCompareTool.slnx"))
                || Directory.Exists(Path.Combine(current.FullName, ".git")))
            {
                return candidate;
            }

            current = current.Parent;
        }

        return Path.Combine(AppContext.BaseDirectory, folderName);
    }

    private void CancelAllBackgroundWork()
    {
        _versionComparisonCts?.Cancel();
        _versionComparisonCts = null;
        CancelCurrentModConflictScan();
    }

    private void CancelCurrentModConflictScan()
    {
        _modConflictCts?.Cancel();
        _modConflictCts = null;
    }

    private void ClearCurrentResults()
    {
        ChangedFiles = [];
        FileTreeNodes = [];
        SelectedFileTreeNode = null;
        _fileTreeNodesByPath = new Dictionary<string, FileTreeNodeViewModel>(StringComparer.OrdinalIgnoreCase);
        DiffLines = [];
        SelectedDiffLine = null;
        ResetDiffNavigation();
        SelectedFile = null;
    }

    private void SetBusy(string message)
    {
        BusyText = message;
        StatusText = message;
        IsBusy = true;
    }

    private void ClearBusy()
    {
        BusyText = string.Empty;
        IsBusy = false;
    }

    private void NotifySelectedFileStateChanged()
    {
        OnPropertyChanged(nameof(HasSelectedFile));
        OnPropertyChanged(nameof(HasNoSelectedFile));
    }

    private void RebuildDiffAreaIndexes()
    {
        var indexes = new List<int>();
        var previousWasDiff = false;

        for (var index = 0; index < DiffLines.Count; index++)
        {
            var isDiff = DiffLines[index].IsDiff;

            if (isDiff && !previousWasDiff)
            {
                indexes.Add(index);
            }

            previousWasDiff = isDiff;
        }

        _diffAreaIndexes = indexes;

        if (_diffAreaIndexes.Count == 0)
        {
            SelectedDiffLine = null;
            ResetDiffNavigation();
            return;
        }

        SelectDiffArea(0);
    }

    private void SelectDiffArea(int diffAreaIndex)
    {
        if (diffAreaIndex < 0 || diffAreaIndex >= _diffAreaIndexes.Count)
        {
            return;
        }

        _currentDiffAreaIndex = diffAreaIndex;
        SelectedDiffLine = DiffLines[_diffAreaIndexes[diffAreaIndex]];
        RefreshDiffNavigationStatus();
    }

    private void UpdateCurrentDiffAreaFromSelectedLine(DiffLineViewModel selectedLine)
    {
        if (_diffAreaIndexes.Count == 0)
        {
            ResetDiffNavigation();
            return;
        }

        var selectedLineIndex = DiffLines.IndexOf(selectedLine);
        if (selectedLineIndex < 0)
        {
            return;
        }

        var nearestAreaIndex = 0;

        for (var index = 0; index < _diffAreaIndexes.Count; index++)
        {
            if (_diffAreaIndexes[index] > selectedLineIndex)
            {
                break;
            }

            nearestAreaIndex = index;
        }

        _currentDiffAreaIndex = nearestAreaIndex;
        RefreshDiffNavigationStatus();
    }

    private void ResetDiffNavigation()
    {
        _diffAreaIndexes = [];
        _currentDiffAreaIndex = -1;
        DiffNavigationStatus = "0 / 0";
        RefreshDiffNavigationCommands();
    }

    private void RefreshDiffNavigationStatus()
    {
        DiffNavigationStatus = _currentDiffAreaIndex >= 0
            ? $"{_currentDiffAreaIndex + 1} / {_diffAreaIndexes.Count}"
            : "0 / 0";
        RefreshDiffNavigationCommands();
    }

    private void RefreshDiffNavigationCommands()
    {
        NavigateToPreviousDiffAreaCommand.NotifyCanExecuteChanged();
        NavigateToNextDiffAreaCommand.NotifyCanExecuteChanged();
    }

    private static ObservableCollection<ChangedFileViewModel> BuildChangedFileViewModels(
        VersionComparison comparison)
    {
        return new ObservableCollection<ChangedFileViewModel>(
            comparison.ChangedFiles.Select(file => new ChangedFileViewModel(file)));
    }

    private void RebuildFileTree()
    {
        var folderNodesByPath = new Dictionary<string, FileTreeNodeViewModel>(StringComparer.OrdinalIgnoreCase);
        var fileNodesByPath = new Dictionary<string, FileTreeNodeViewModel>(StringComparer.OrdinalIgnoreCase);
        var roots = new ObservableCollection<FileTreeNodeViewModel>();

        foreach (var file in ChangedFiles.OrderBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase))
        {
            var normalizedPath = file.RelativePath.Replace('\\', '/');
            var segments = normalizedPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length == 0)
            {
                continue;
            }

            FileTreeNodeViewModel? parent = null;
            var folderPath = string.Empty;

            for (var index = 0; index < segments.Length; index++)
            {
                var segment = segments[index];
                var isFile = index == segments.Length - 1;

                if (isFile)
                {
                    var fileNode = new FileTreeNodeViewModel(segment, parent, file);
                    AddFileTreeNode(roots, parent, fileNode);
                    fileNodesByPath[normalizedPath] = fileNode;
                    continue;
                }

                folderPath = string.IsNullOrWhiteSpace(folderPath)
                    ? segment
                    : $"{folderPath}/{segment}";

                if (!folderNodesByPath.TryGetValue(folderPath, out var folderNode))
                {
                    folderNode = new FileTreeNodeViewModel(segment, parent);
                    folderNodesByPath[folderPath] = folderNode;
                    AddFileTreeNode(roots, parent, folderNode);
                }

                parent = folderNode;
            }
        }

        SortFileTreeNodes(roots);
        _fileTreeNodesByPath = fileNodesByPath;
        FileTreeNodes = roots;
    }

    private static void AddFileTreeNode(
        ObservableCollection<FileTreeNodeViewModel> roots,
        FileTreeNodeViewModel? parent,
        FileTreeNodeViewModel node)
    {
        if (parent is null)
        {
            roots.Add(node);
            return;
        }

        parent.Children.Add(node);
    }

    private static void SortFileTreeNodes(ObservableCollection<FileTreeNodeViewModel> nodes)
    {
        var sortedNodes = nodes
            .OrderByDescending(node => node.IsFolder)
            .ThenBy(node => node.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        nodes.Clear();

        foreach (var node in sortedNodes)
        {
            nodes.Add(node);
            SortFileTreeNodes(node.Children);
        }
    }

    private void SyncSelectedFileTreeNode(ChangedFileViewModel? file)
    {
        if (_isApplyingTreeSelection)
        {
            return;
        }

        _isSyncingTreeSelection = true;

        try
        {
            if (file is null || !_fileTreeNodesByPath.TryGetValue(file.RelativePath, out var node))
            {
                SelectedFileTreeNode = null;
                return;
            }

            ExpandAncestors(node);
            SelectedFileTreeNode = node;
        }
        finally
        {
            _isSyncingTreeSelection = false;
        }
    }

    private static void ExpandAncestors(FileTreeNodeViewModel node)
    {
        var current = node.Parent;
        while (current is not null)
        {
            current.IsExpanded = true;
            current = current.Parent;
        }
    }

    private static string BuildSelectedFileSummary(ChangedFileViewModel file)
    {
        var parts = new List<string> { file.ChangeTypeText };

        if (file.Model.Additions > 0)
        {
            parts.Add($"+{file.Model.Additions} additions");
        }

        if (file.Model.Deletions > 0)
        {
            parts.Add($"-{file.Model.Deletions} deletions");
        }

        if (file.Model.HasModConflicts)
        {
            parts.Add(file.ModConflictSummary);
        }

        return string.Join(" | ", parts);
    }

    private static string BuildStatusText(VersionComparison comparison)
    {
        var status = $"{comparison.StartVersion} to {comparison.EndVersion}: "
            + $"{comparison.ModifiedFiles} modified, "
            + $"{comparison.AddedFiles} added, "
            + $"{comparison.RemovedFiles} removed | "
            + $"+{comparison.TotalAdditions} -{comparison.TotalDeletions} | "
            + (comparison.IsFromCache ? "Loaded from cache" : "Cache refreshed");

        if (!string.IsNullOrWhiteSpace(comparison.ModName))
        {
            status += $" | {comparison.ModName}: {comparison.ModConflictFiles} conflict files";
        }
        else
        {
            status += " | No mod selected";
        }

        return status;
    }

    private sealed record FolderLoadResult(
        IReadOnlyList<string> Versions,
        IReadOnlyList<string> Mods);

    private sealed record VersionComparisonRequest(
        string StartVersion,
        string EndVersion,
        string StartDirectory,
        string EndDirectory);

    private sealed record ModConflictRequest(
        string ModName,
        string ModDirectory);
}
