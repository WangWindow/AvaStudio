using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FluentIcons.Common;
using AvaStudio.Helpers;
using AvaStudio.Models;
using AvaStudio.Services;
using AvaStudio.ViewModels.Services;

namespace AvaStudio.ViewModels.Pages;

/// <summary>
/// 播放队列页：显示真实运行时队列，支持播放、移除、清空、多选和添加到播放列表。
/// 队列本身由 <see cref="Services.Playback.PlaybackCoordinator"/> 持有，本页只做展示与命令转发。
/// </summary>
public sealed partial class QueuePageViewModel : PageViewModelBase
{
    private readonly AppServices _services;
    private readonly Action<string> _reportStatus;
    private bool _rebuildScheduled;

    public QueuePageViewModel(AppServices services, Action<string> reportStatus)
        : base("播放队列", Symbol.DocumentQueue)
    {
        _services = services;
        _reportStatus = reportStatus;
        _services.Playback.Queue.CollectionChanged += OnQueueChanged;
        _services.Playback.CurrentItemChanged += OnCurrentItemChanged;
        _services.Playlists.Changed += OnPlaylistsChanged;
        Rebuild();
        RebuildPlaylistTargets();
    }

    /// <summary>队列界面条目。</summary>
    public ObservableCollection<MediaItemViewModel> Items { get; } = [];

    /// <summary>可添加到的播放列表。</summary>
    public ObservableCollection<PlaylistTargetViewModel> PlaylistTargets { get; } = [];

    /// <summary>是否有条目。</summary>
    public bool HasItems => Items.Count > 0;

    /// <summary>是否为空。</summary>
    public bool IsEmpty => !HasItems;

    /// <summary>是否可以添加到播放列表。</summary>
    public bool CanAddToPlaylist => HasItems && PlaylistTargets.Count > 0;

    /// <summary>队列摘要。</summary>
    public string SummaryText => Items.Count == 0
        ? "空 · 0:00 播放时长"
        : $"{Items.Count} 个项目 · {DurationFormatter.FormatLong(TotalDuration)}";

    /// <summary>队列总时长。</summary>
    public TimeSpan TotalDuration => _services.Playback.Queue.Aggregate(
        TimeSpan.Zero,
        static (sum, item) => sum + item.Duration);

    /// <summary>添加到播放列表按钮提示。</summary>
    public string AddToPlaylistHint => !HasItems
        ? "播放队列为空"
        : PlaylistTargets.Count == 0
            ? "先创建一个播放列表"
            : "添加到播放列表";

    /// <summary>多选模式。</summary>
    [ObservableProperty]
    public partial bool IsMultiSelect { get; set; }

    /// <summary>已选中的条目数量。</summary>
    public int SelectedCount => Items.Count(static item => item.IsSelected);

    /// <summary>是否有选中项。</summary>
    public bool HasSelection => SelectedCount > 0;

    partial void OnIsMultiSelectChanged(bool value)
    {
        foreach (var entry in Items)
        {
            entry.IsSelectionVisible = value;
            if (!value)
            {
                entry.IsSelected = false;
            }
        }

        RaiseSelectionProperties();
    }

    /// <summary>添加文件到队列。</summary>
    [RelayCommand]
    private async Task AddFilesAsync()
    {
        var files = await _services.Desktop.PickMediaFilesAsync().ConfigureAwait(true);
        await AddPathsAsync(files).ConfigureAwait(true);
    }

    /// <summary>添加文件夹到队列。</summary>
    [RelayCommand]
    private async Task AddFolderAsync()
    {
        var folder = await _services.Desktop.PickFolderAsync("添加文件夹到播放队列").ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(folder))
        {
            return;
        }

        var files = await Task.Run(() => DesktopService.EnumerateMediaFiles(folder)).ConfigureAwait(true);
        await AddPathsAsync(files).ConfigureAwait(true);
    }

    /// <summary>
    /// 把文件路径解析为元数据后追加到队列。路径会先展开文件夹并去重；
    /// 标签读取在后台线程完成，界面保持响应。
    /// </summary>
    public async Task<int> AddPathsAsync(IEnumerable<string> paths)
    {
        var pathList = paths as IReadOnlyList<string> ?? paths.ToArray();
        var expanded = await Task.Run(() => DesktopService.ExpandMediaPaths(pathList)).ConfigureAwait(true);
        if (expanded.Count == 0)
        {
            _reportStatus("所选项目中没有可用的媒体文件。");
            return 0;
        }

        var items = new List<MediaItem>(expanded.Count);
        foreach (var path in expanded)
        {
            try
            {
                items.Add(await _services.Library.LoadItemAsync(path).ConfigureAwait(true));
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _reportStatus($"无法读取“{System.IO.Path.GetFileName(path)}”：{exception.Message}");
            }
        }

        var added = _services.Playback.AddToQueue(items);
        _reportStatus(added > 0
            ? $"已添加 {added} 个文件到播放队列。"
            : "这些文件已在播放队列中。");
        return added;
    }

    /// <summary>清空播放队列。</summary>
    [RelayCommand(CanExecute = nameof(CanClear))]
    private async Task ClearAsync()
    {
        await _services.Playback.ClearQueueAsync().ConfigureAwait(true);
        IsMultiSelect = false;
        _reportStatus("已清空播放队列。");
    }

    private bool CanClear() => HasItems;

    /// <summary>移除当前选中的条目。</summary>
    [RelayCommand(CanExecute = nameof(CanRemoveSelected))]
    private void RemoveSelected()
    {
        var selected = Items.Where(static item => item.IsSelected).Select(static item => item.Item).ToArray();
        foreach (var item in selected)
        {
            _services.Playback.RemoveFromQueue(item);
        }

        _reportStatus(selected.Length > 0 ? $"已从队列移除 {selected.Length} 个项目。" : "请先选择要移除的项目。");
    }

    private bool CanRemoveSelected() => HasSelection;

    /// <summary>播放队列中的条目。</summary>
    private async Task PlayQueueItemAsync(MediaItemViewModel entry)
    {
        var context = _services.Playback.Queue.ToArray();
        await _services.Playback.PlayAsync(entry.Item, context).ConfigureAwait(true);
    }

    /// <summary>从队列移除一个条目。</summary>
    private void RemoveQueueItem(MediaItemViewModel entry)
    {
        if (_services.Playback.RemoveFromQueue(entry.Item))
        {
            _reportStatus($"已从队列移除“{entry.Title}”。");
        }
    }

    /// <summary>把队列条目添加到播放列表。</summary>
    private void AddToPlaylist(PlaylistModel playlist)
    {
        MediaItem[] source;
        if (IsMultiSelect)
        {
            source = Items.Where(static item => item.IsSelected)
                .Select(static item => item.Item)
                .ToArray();
            if (source.Length == 0)
            {
                _reportStatus("请先选择要添加的项目。");
                return;
            }
        }
        else
        {
            source = _services.Playback.Queue.ToArray();
        }

        var added = _services.Playlists.AddItems(playlist, source);
        _reportStatus(added > 0
            ? $"已向“{playlist.Name}”添加 {added} 个项目。"
            : "这些文件已在播放列表中。");
    }

    private void Rebuild()
    {
        Items.Clear();
        foreach (var item in _services.Playback.Queue)
        {
            Items.Add(CreateEntry(item));
        }

        RefreshCurrent();
        RaiseQueueProperties();
    }

    private MediaItemViewModel CreateEntry(MediaItem item)
    {
        var viewModel = new MediaItemViewModel(
            item,
            _services.ArtworkLoader,
            entry => _ = PlayQueueItemAsync(entry),
            RemoveQueueItem)
        {
            IsSelectionVisible = IsMultiSelect,
        };
        viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(MediaItemViewModel.IsSelected))
            {
                RaiseSelectionProperties();
            }
        };
        _ = viewModel.LoadArtworkAsync(64);
        return viewModel;
    }

    private void RefreshCurrent()
    {
        var current = _services.Playback.CurrentItem;
        foreach (var entry in Items)
        {
            entry.SetCurrent(current is not null &&
                string.Equals(entry.Path, current.Path, StringComparison.OrdinalIgnoreCase));
        }
    }

    private void RaiseQueueProperties()
    {
        OnPropertyChanged(nameof(HasItems));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(CanAddToPlaylist));
        OnPropertyChanged(nameof(SummaryText));
        OnPropertyChanged(nameof(AddToPlaylistHint));
        OnPropertyChanged(nameof(TotalDuration));
        RaiseSelectionProperties();
        ClearCommand.NotifyCanExecuteChanged();
    }

    private void RaiseSelectionProperties()
    {
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(HasSelection));
        RemoveSelectedCommand.NotifyCanExecuteChanged();
    }

    private void OnQueueChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        // 替换队列时会产生多次集合事件；合并到下一次 UI 调度，避免 O(n²) 重建。
        if (_rebuildScheduled)
        {
            return;
        }

        _rebuildScheduled = true;
        Dispatcher.UIThread.Post(
            () =>
            {
                _rebuildScheduled = false;
                Rebuild();
            },
            DispatcherPriority.Background);
    }

    private void OnCurrentItemChanged(object? sender, EventArgs e)
    {
        RefreshCurrent();
    }

    private void OnPlaylistsChanged(object? sender, EventArgs e)
    {
        RebuildPlaylistTargets();
    }

    private void RebuildPlaylistTargets()
    {
        PlaylistTargets.Clear();
        foreach (var playlist in _services.Playlists.Playlists)
        {
            PlaylistTargets.Add(new PlaylistTargetViewModel(
                playlist.Name,
                new RelayCommand(() => AddToPlaylist(playlist))));
        }

        OnPropertyChanged(nameof(CanAddToPlaylist));
        OnPropertyChanged(nameof(AddToPlaylistHint));
    }
}

/// <summary>播放列表目标项的界面模型。</summary>
public sealed class PlaylistTargetViewModel
{
    public PlaylistTargetViewModel(string name, IRelayCommand addCommand)
    {
        Name = name;
        AddCommand = addCommand;
    }

    public string Name { get; }

    public IRelayCommand AddCommand { get; }
}
