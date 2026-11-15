using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FluentIcons.Common;
using AvaStudio.Services;
using AvaStudio.ViewModels.Items;

namespace AvaStudio.ViewModels.Pages;

/// <summary>主页：展示最近播放，并提供打开文件/文件夹的入口。</summary>
public sealed partial class HomePageViewModel : PageViewModelBase
{
    /// <summary>主页最多展示的最近条目数。</summary>
    private const int RecentDisplayLimit = 12;

    private readonly AppServices _services;
    private readonly Func<Task> _openFiles;
    private readonly Func<Task> _openFolder;

    public HomePageViewModel(
        AppServices services,
        Func<Task> openFiles,
        Func<Task> openFolder)
        : base("主页", Symbol.Home)
    {
        _services = services;
        _openFiles = openFiles;
        _openFolder = openFolder;
        _services.Library.RecentChanged += OnRecentChanged;
        RebuildRecent();
    }

    /// <summary>最近播放条目。</summary>
    public ObservableCollection<MediaItemViewModel> RecentItems { get; } = [];

    /// <summary>是否存在最近播放。</summary>
    public bool HasRecent => RecentItems.Count > 0;

    /// <summary>是否显示空状态。</summary>
    public bool IsEmpty => !HasRecent;

    /// <summary>最近播放数量文本。</summary>
    public string RecentCountText => RecentItems.Count == 0
        ? string.Empty
        : $"{RecentItems.Count} 个最近项目";

    /// <summary>资料库统计文本。</summary>
    public string LibrarySummary
    {
        get
        {
            var snapshot = _services.Library.Snapshot;
            return $"{snapshot.MusicItems.Count} 首歌曲 · {snapshot.Albums.Count} 张专辑 · {snapshot.VideoItems.Count} 个视频";
        }
    }

    /// <summary>打开文件命令。</summary>
    [RelayCommand]
    private Task OpenFilesAsync() => _openFiles();

    /// <summary>打开文件夹命令。</summary>
    [RelayCommand]
    private Task OpenFolderAsync() => _openFolder();

    /// <summary>播放最近条目。</summary>
    private async Task PlayRecentAsync(MediaItemViewModel item)
    {
        var context = RecentItems.Select(static entry => entry.Item).ToArray();
        await _services.Playback.PlayAsync(item.Item, context).ConfigureAwait(true);
    }

    /// <summary>重建最近播放列表。</summary>
    private void RebuildRecent()
    {
        RecentItems.Clear();
        foreach (var item in _services.Library.RecentItems.Take(RecentDisplayLimit))
        {
            var viewModel = new MediaItemViewModel(
                item,
                _services.ArtworkLoader,
                vm => _ = PlayRecentAsync(vm));
            _ = viewModel.LoadArtworkAsync();
            RecentItems.Add(viewModel);
        }

        OnPropertyChanged(nameof(HasRecent));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(RecentCountText));
        OnPropertyChanged(nameof(LibrarySummary));
    }

    private void OnRecentChanged(object? sender, EventArgs e)
    {
        RebuildRecent();
    }
}
