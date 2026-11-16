using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FluentIcons.Common;
using AvaStudio.Models;
using AvaStudio.Services;
using AvaStudio.Services.Library;
using AvaStudio.Services.Settings;
using AvaStudio.ViewModels.Items;

namespace AvaStudio.ViewModels.Pages;

/// <summary>视频页的两个分区。</summary>
public enum VideoSection
{
    Folders,
    AllVideos,
}

/// <summary>
/// 视频页：按资料库目录树浏览视频，或展示全部视频网格。
/// 目录树保留空文件夹，并在文件夹页同时显示当前目录的子文件夹和直接视频。
/// </summary>
public sealed partial class VideosPageViewModel : PageViewModelBase
{
    private readonly AppServices _services;
    private readonly Action<string> _reportStatus;
    private VideoDirectoryNode? _currentFolder;
    private bool _isUpdatingSortDirection;

    public VideosPageViewModel(AppServices services, Action<string> reportStatus)
        : base("视频", Symbol.Video)
    {
        _services = services;
        _reportStatus = reportStatus;
        _services.Library.Changed += OnLibraryChanged;
        _services.Settings.Changed += OnSettingsChanged;
        Rebuild();
    }

    /// <summary>当前文件夹的子文件夹。</summary>
    public ObservableCollection<VideoFolderItemViewModel> Folders { get; } = [];

    /// <summary>全部视频。</summary>
    public ObservableCollection<MediaItemViewModel> AllVideos { get; } = [];

    /// <summary>当前文件夹中的直接视频。</summary>
    public ObservableCollection<MediaItemViewModel> FolderVideos { get; } = [];

    /// <summary>当前目录的路径导航；从“视频”到当前文件夹。</summary>
    public ObservableCollection<VideoBreadcrumbItem> Breadcrumbs { get; } = [];

    /// <summary>是否显示路径导航。</summary>
    public bool HasBreadcrumbs => Breadcrumbs.Count > 0;

    /// <summary>当前分区。</summary>
    [ObservableProperty]
    public partial VideoSection SelectedSection { get; set; } = VideoSection.Folders;

    /// <summary>视频排序选项。</summary>
    public IReadOnlyList<string> SortOptions { get; } = ["名称", "时间"];

    /// <summary>当前排序索引：0 名称，1 文件时间（最新优先）。</summary>
    [ObservableProperty]
    public partial int SelectedSortIndex { get; set; }

    /// <summary>是否降序；名称默认升序，时间默认降序。</summary>
    [ObservableProperty]
    public partial bool IsSortDescending { get; set; }

    /// <summary>排序方向图标：单箭头突出当前升/降序。</summary>
    public Symbol SortSymbol => IsSortDescending ? Symbol.ArrowSortDown : Symbol.ArrowSortUp;

    /// <summary>排序方向提示。</summary>
    public string SortDirectionTip => IsSortDescending ? "当前降序，点击切换为升序" : "当前升序，点击切换为降序";

    /// <summary>当前文件夹路径；null 表示没有可浏览的资料库根目录。</summary>
    [ObservableProperty]
    public partial string? OpenedFolderPath { get; set; }

    /// <summary>当前文件夹名称。</summary>
    [ObservableProperty]
    public partial string OpenedFolderName { get; set; } = string.Empty;

    /// <summary>是否显示当前文件夹的子文件夹区域。</summary>
    public bool IsFolderListVisible => SelectedSection == VideoSection.Folders && _currentFolder is not null;

    /// <summary>是否位于资料库根目录之外的子文件夹，用于显示返回按钮。</summary>
    public bool IsFolderOpen => SelectedSection == VideoSection.Folders
        && _currentFolder is not null
        && !IsLibraryRoot(_currentFolder.Path);

    /// <summary>是否选中文件夹分区。</summary>
    public bool IsFoldersSelected => SelectedSection == VideoSection.Folders;

    /// <summary>是否选中全部视频分区。</summary>
    public bool IsAllVideosSelected => SelectedSection == VideoSection.AllVideos;

    /// <summary>视频资料库位置数量。</summary>
    public int VideoLocationCount => _services.Settings.VideoFolders.Count;

    /// <summary>是否有资料库位置。</summary>
    public bool HasLibraryLocations => VideoLocationCount > 0;

    /// <summary>当前文件夹是否有子文件夹。</summary>
    public bool HasFolders => Folders.Count > 0;

    /// <summary>是否有全部视频。</summary>
    public bool HasAllVideos => AllVideos.Count > 0;

    /// <summary>当前文件夹是否有直接视频。</summary>
    public bool HasFolderVideos => FolderVideos.Count > 0;

    /// <summary>当前视图是否有内容。</summary>
    public bool HasContent => SelectedSection switch
    {
        VideoSection.AllVideos => AllVideos.Count > 0,
        _ => _currentFolder is not null && (Folders.Count > 0 || FolderVideos.Count > 0),
    };

    /// <summary>是否显示空状态。</summary>
    public bool IsEmpty => !HasContent;

    /// <summary>空状态标题。</summary>
    public string EmptyTitle => SelectedSection == VideoSection.AllVideos
        ? "还没有视频"
        : !HasLibraryLocations
            ? "还没有视频文件夹"
            : _currentFolder is null ? "没有可浏览的视频目录" : "此文件夹为空";

    /// <summary>空状态说明。</summary>
    public string EmptyDescription => HasLibraryLocations
        ? "此目录暂时没有子文件夹或视频；可以返回上级目录或添加新的视频文件夹。"
        : "添加视频文件夹后会扫描其中的视频并在本页显示。";

    /// <summary>空状态图标。</summary>
    public Symbol SectionSymbol => SelectedSection == VideoSection.AllVideos
        ? Symbol.Filmstrip
        : Symbol.Folder;

    /// <summary>视频总数文本。</summary>
    public string VideoCountText => $"{AllVideos.Count} 个视频";

    /// <summary>当前文件夹的子文件夹数量文本。</summary>
    public string FolderCountText => $"{Folders.Count} 个文件夹";

    /// <summary>当前文件夹标题；资料库根目录显示统一的“视频”。</summary>
    public string FolderHeading => _currentFolder is null || IsLibraryRoot(_currentFolder.Path)
        ? "视频"
        : _currentFolder.Name;

    /// <summary>页面大标题：文件夹分区显示当前目录名，全部视频分区显示分区名。</summary>
    public string PageHeading => SelectedSection == VideoSection.AllVideos
        ? "所有视频"
        : FolderHeading;

    partial void OnSelectedSectionChanged(VideoSection value)
    {
        RaiseViewProperties();
    }

    partial void OnSelectedSortIndexChanged(int value)
    {
        // 名称默认升序，时间默认最新优先；切换排序方式时恢复该方式的默认方向。
        _isUpdatingSortDirection = true;
        IsSortDescending = value == 1;
        _isUpdatingSortDirection = false;
        Rebuild();
    }

    partial void OnIsSortDescendingChanged(bool value)
    {
        if (!_isUpdatingSortDirection)
        {
            Rebuild();
        }

        OnPropertyChanged(nameof(SortSymbol));
        OnPropertyChanged(nameof(SortDirectionTip));
    }

    /// <summary>切换升序/降序。</summary>
    [RelayCommand]
    private void ToggleSortDirection() => IsSortDescending = !IsSortDescending;

    partial void OnOpenedFolderPathChanged(string? value)
    {
        RaiseViewProperties();
    }

    /// <summary>切换文件夹分区。</summary>
    [RelayCommand]
    private void SelectFolders() => SelectedSection = VideoSection.Folders;

    /// <summary>切换全部视频分区。</summary>
    [RelayCommand]
    private void SelectAllVideos() => SelectedSection = VideoSection.AllVideos;

    /// <summary>打开一个子文件夹。</summary>
    private void OpenFolder(VideoFolderItemViewModel folder)
    {
        SelectedSection = VideoSection.Folders;
        SetCurrentFolder(folder.Node);
    }

    /// <summary>返回上一级目录；根目录没有返回动作。</summary>
    [RelayCommand]
    private void BackToFolders()
    {
        if (_currentFolder?.ParentPath is not { } parentPath)
        {
            return;
        }

        var parent = FindNode(_services.Library.Snapshot.VideoRoots, parentPath);
        if (parent is not null)
        {
            SetCurrentFolder(parent);
        }
    }

    /// <summary>点击路径导航中的一节，直接跳到该目录。</summary>
    private void NavigateToBreadcrumb(VideoDirectoryNode node)
    {
        SelectedSection = VideoSection.Folders;
        SetCurrentFolder(node);
    }

    /// <summary>按当前目录向上构建“视频 › 根目录 › … › 当前目录”的路径。</summary>
    private void RebuildBreadcrumbs(VideoDirectoryNode? folder)
    {
        Breadcrumbs.Clear();
        if (folder is null)
        {
            OnPropertyChanged(nameof(HasBreadcrumbs));
            return;
        }

        var roots = _services.Library.Snapshot.VideoRoots;
        var chain = new List<VideoDirectoryNode>();
        for (var node = folder; node is not null;)
        {
            chain.Add(node);
            if (node.ParentPath is null)
            {
                break;
            }

            node = FindNode(roots, node.ParentPath);
        }

        chain.Reverse();
        if (chain.Count == 0)
        {
            OnPropertyChanged(nameof(HasBreadcrumbs));
            return;
        }

        // 第一节固定显示“视频”，点击回到当前资料库根目录。
        Breadcrumbs.Add(new VideoBreadcrumbItem("视频", chain[0], isFirst: true, isCurrent: false, NavigateToBreadcrumb));

        // 资料库根目录（例如 Videos）不单独占一节：子文件夹显示为“视频 › OBS”，
        // 只有当前就在资料库根目录时才显示根目录名。
        var displayed = chain.Count > 1 ? chain.Skip(1) : chain;
        foreach (var item in displayed)
        {
            Breadcrumbs.Add(new VideoBreadcrumbItem(
                item.Name,
                item,
                isFirst: false,
                isCurrent: ReferenceEquals(item, folder),
                NavigateToBreadcrumb));
        }

        OnPropertyChanged(nameof(HasBreadcrumbs));
    }

    /// <summary>添加视频文件夹并触发扫描。</summary>
    [RelayCommand]
    private async Task AddVideoFolderAsync()
    {
        var folder = await _services.Desktop.PickFolderAsync("添加视频文件夹").ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(folder))
        {
            return;
        }

        var result = _services.Settings.AddVideoFolder(folder);
        _reportStatus(result switch
        {
            SettingsAddResult.Added => "已添加视频资料库位置，正在扫描…",
            SettingsAddResult.Duplicate => "该文件夹已在视频资料库中。",
            SettingsAddResult.SaveFailed => "无法保存资料库位置，请检查本地设置目录。",
            _ => "无法使用该文件夹位置。",
        });

        if (result == SettingsAddResult.Added)
        {
            await _services.Library.RefreshAsync().ConfigureAwait(true);
        }
    }

    /// <summary>播放单个视频。</summary>
    private async Task PlayVideoAsync(
        MediaItemViewModel video,
        IReadOnlyList<MediaItem> context)
    {
        await _services.Playback.PlayAsync(video.Item, context).ConfigureAwait(true);
    }

    private MediaItemViewModel CreateVideoViewModel(
        MediaItem item,
        IReadOnlyList<MediaItem> context)
    {
        var viewModel = new MediaItemViewModel(
            item,
            _services.ArtworkLoader,
            video => _ = PlayVideoAsync(video, context));
        _ = viewModel.LoadArtworkAsync();
        return viewModel;
    }

    /// <summary>根据资料库快照重建目录列表、直接视频与全部视频。</summary>
    private void Rebuild()
    {
        var snapshot = _services.Library.Snapshot;

        AllVideos.Clear();
        var allVideoItems = SortVideoItems(snapshot.VideoItems).ToArray();
        foreach (var item in allVideoItems)
        {
            AllVideos.Add(CreateVideoViewModel(item, allVideoItems));
        }

        var current = OpenedFolderPath is not null
            ? FindNode(snapshot.VideoRoots, OpenedFolderPath)
            : null;
        current ??= snapshot.VideoRoots.FirstOrDefault();
        SetCurrentFolder(current, rebuild: true);

        RaiseViewProperties();
    }

    /// <summary>更新当前文件夹并同步子文件夹、直接视频与标题。</summary>
    private void SetCurrentFolder(VideoDirectoryNode? folder, bool rebuild = false)
    {
        _currentFolder = folder;
        OpenedFolderPath = folder?.Path;
        OpenedFolderName = folder?.Name ?? string.Empty;
        RebuildBreadcrumbs(folder);

        Folders.Clear();
        FolderVideos.Clear();
        if (folder is not null)
        {
            // 参考 Screenbox：非空文件夹优先，数量相同再按拼音名称排序。
            foreach (var child in folder.Children
                         .OrderByDescending(static child => child.ItemCount)
                         .ThenBy(static child => LibraryTextSort.GetSortKey(child.Name), StringComparer.OrdinalIgnoreCase)
                         .ThenBy(static child => child.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                Folders.Add(new VideoFolderItemViewModel(child, OpenFolder));
            }

            var folderVideoItems = SortVideoItems(folder.Videos).ToArray();
            foreach (var item in folderVideoItems)
            {
                FolderVideos.Add(CreateVideoViewModel(item, folderVideoItems));
            }
        }

        if (!rebuild)
        {
            RaiseViewProperties();
        }
    }

    /// <summary>判断路径是否为资料库视频根目录。</summary>
    private bool IsLibraryRoot(string path) => _services.Library.Snapshot.VideoRoots
        .Any(root => string.Equals(root.Path, path, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 按当前排序选项排列视频：名称使用拼音排序键；时间按文件修改时间最新优先，
    /// 修改时间相同时用名称兜底，保证结果稳定。
    /// </summary>
    private IEnumerable<MediaItem> SortVideoItems(IEnumerable<MediaItem> items) =>
        SelectedSortIndex == 1
            ? (IsSortDescending
                    ? items.OrderByDescending(static item => item.FileModifiedTicks)
                    : items.OrderBy(static item => item.FileModifiedTicks))
                .ThenBy(static item => LibraryTextSort.GetSortKey(item.DisplayTitle), StringComparer.OrdinalIgnoreCase)
                .ThenBy(static item => item.DisplayTitle, StringComparer.CurrentCultureIgnoreCase)
            : (IsSortDescending
                    ? items.OrderByDescending(static item => LibraryTextSort.GetSortKey(item.DisplayTitle), StringComparer.OrdinalIgnoreCase)
                    : items.OrderBy(static item => LibraryTextSort.GetSortKey(item.DisplayTitle), StringComparer.OrdinalIgnoreCase))
                .ThenBy(static item => item.DisplayTitle, StringComparer.CurrentCultureIgnoreCase);

    /// <summary>在目录树中查找路径对应节点。</summary>
    private static VideoDirectoryNode? FindNode(IReadOnlyList<VideoDirectoryNode> roots, string path)
    {
        foreach (var root in roots)
        {
            var found = FindNode(root, path);
            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }

    private static VideoDirectoryNode? FindNode(VideoDirectoryNode node, string path)
    {
        if (string.Equals(node.Path, path, StringComparison.OrdinalIgnoreCase))
        {
            return node;
        }

        foreach (var child in node.Children)
        {
            var found = FindNode(child, path);
            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }

    private void RaiseViewProperties()
    {
        OnPropertyChanged(nameof(IsFolderListVisible));
        OnPropertyChanged(nameof(IsFolderOpen));
        OnPropertyChanged(nameof(IsFoldersSelected));
        OnPropertyChanged(nameof(IsAllVideosSelected));
        OnPropertyChanged(nameof(HasFolders));
        OnPropertyChanged(nameof(HasAllVideos));
        OnPropertyChanged(nameof(HasFolderVideos));
        OnPropertyChanged(nameof(HasContent));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(EmptyTitle));
        OnPropertyChanged(nameof(EmptyDescription));
        OnPropertyChanged(nameof(SectionSymbol));
        OnPropertyChanged(nameof(VideoCountText));
        OnPropertyChanged(nameof(FolderCountText));
        OnPropertyChanged(nameof(FolderHeading));
        OnPropertyChanged(nameof(PageHeading));
        OnPropertyChanged(nameof(VideoLocationCount));
        OnPropertyChanged(nameof(HasLibraryLocations));
    }

    private void OnLibraryChanged(object? sender, EventArgs e)
    {
        Rebuild();
    }

    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(VideoLocationCount));
        OnPropertyChanged(nameof(HasLibraryLocations));
    }
}
