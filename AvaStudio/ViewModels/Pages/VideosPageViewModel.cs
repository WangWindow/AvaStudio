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

    /// <summary>当前分区。</summary>
    [ObservableProperty]
    public partial VideoSection SelectedSection { get; set; } = VideoSection.Folders;

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

    /// <summary>资料库位置摘要。</summary>
    public string LocationSummary => VideoLocationCount == 0
        ? "尚未添加视频文件夹"
        : $"已指定 {VideoLocationCount} 个资料库位置";

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

    partial void OnSelectedSectionChanged(VideoSection value)
    {
        RaiseViewProperties();
    }

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
        foreach (var item in snapshot.VideoItems)
        {
            AllVideos.Add(CreateVideoViewModel(item, snapshot.VideoItems));
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

        Folders.Clear();
        FolderVideos.Clear();
        if (folder is not null)
        {
            foreach (var child in folder.Children)
            {
                Folders.Add(new VideoFolderItemViewModel(child, OpenFolder));
            }

            foreach (var item in folder.Videos)
            {
                FolderVideos.Add(CreateVideoViewModel(item, folder.Videos));
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
        OnPropertyChanged(nameof(VideoLocationCount));
        OnPropertyChanged(nameof(HasLibraryLocations));
        OnPropertyChanged(nameof(LocationSummary));
    }

    private void OnLibraryChanged(object? sender, EventArgs e)
    {
        Rebuild();
    }

    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(VideoLocationCount));
        OnPropertyChanged(nameof(HasLibraryLocations));
        OnPropertyChanged(nameof(LocationSummary));
    }
}
