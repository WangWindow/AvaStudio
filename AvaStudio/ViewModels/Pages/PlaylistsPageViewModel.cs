using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FluentIcons.Common;
using AvaStudio.Helpers;
using AvaStudio.Services;
using AvaStudio.ViewModels.Items;

namespace AvaStudio.ViewModels.Pages;

/// <summary>
/// 播放列表页：创建/删除列表、查看详情、播放整个列表以及移除条目。
/// 所有持久化操作都委托给 <see cref="Services.Playlists.PlaylistStore"/>。
/// </summary>
public sealed partial class PlaylistsPageViewModel : PageViewModelBase
{
    /// <summary>播放列表名称最大长度。</summary>
    private const int MaxNameLength = 64;

    private readonly AppServices _services;
    private readonly Action<string> _reportStatus;

    public PlaylistsPageViewModel(AppServices services, Action<string> reportStatus)
        : base("播放列表", Symbol.Collections)
    {
        _services = services;
        _reportStatus = reportStatus;
        _services.Playlists.Changed += OnStoreChanged;
        Rebuild();
    }

    /// <summary>播放列表卡片。</summary>
    public ObservableCollection<PlaylistItemViewModel> Playlists { get; } = [];

    /// <summary>详情页条目。</summary>
    public ObservableCollection<MediaItemViewModel> DetailItems { get; } = [];

    /// <summary>是否有播放列表。</summary>
    public bool HasPlaylists => Playlists.Count > 0;

    /// <summary>是否为空。</summary>
    public bool IsEmpty => !HasPlaylists;

    /// <summary>创建表单是否打开。</summary>
    [ObservableProperty]
    public partial bool IsCreateFormOpen { get; set; }

    /// <summary>新建列表名称。</summary>
    [ObservableProperty]
    public partial string NewPlaylistName { get; set; } = string.Empty;

    /// <summary>当前打开的列表；null 表示卡片列表。</summary>
    [ObservableProperty]
    public partial PlaylistItemViewModel? SelectedPlaylist { get; set; }

    /// <summary>是否位于详情页。</summary>
    public bool IsDetailOpen => SelectedPlaylist is not null;

    /// <summary>详情页标题。</summary>
    public string DetailTitle => SelectedPlaylist?.Name ?? string.Empty;

    /// <summary>详情页统计。</summary>
    public string DetailSummary => SelectedPlaylist is null
        ? string.Empty
        : $"{SelectedPlaylist.Model.Items.Count} 个项目 · {DurationFormatter.FormatLong(SelectedPlaylist.Model.TotalDuration)}";

    /// <summary>详情页是否为空。</summary>
    public bool IsDetailEmpty => DetailItems.Count == 0;

    /// <summary>详情页是否有条目。</summary>
    public bool HasDetailItems => DetailItems.Count > 0;

    /// <summary>是否可以创建播放列表。</summary>
    public bool CanCreatePlaylist => !string.IsNullOrWhiteSpace(NewPlaylistName);

    partial void OnNewPlaylistNameChanged(string value)
    {
        OnPropertyChanged(nameof(CanCreatePlaylist));
    }

    partial void OnSelectedPlaylistChanged(PlaylistItemViewModel? value)
    {
        RebuildDetail();
        OnPropertyChanged(nameof(IsDetailOpen));
        OnPropertyChanged(nameof(DetailTitle));
        OnPropertyChanged(nameof(DetailSummary));
        OnPropertyChanged(nameof(IsDetailEmpty));
        OnPropertyChanged(nameof(HasDetailItems));
    }

    /// <summary>打开创建表单。</summary>
    [RelayCommand]
    private void BeginCreatePlaylist()
    {
        IsCreateFormOpen = true;
    }

    /// <summary>取消创建。</summary>
    [RelayCommand]
    private void CancelCreatePlaylist()
    {
        IsCreateFormOpen = false;
        NewPlaylistName = string.Empty;
    }

    /// <summary>创建播放列表。</summary>
    [RelayCommand]
    private void CreatePlaylist()
    {
        var name = NewPlaylistName.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        if (name.Length > MaxNameLength)
        {
            name = name[..MaxNameLength];
        }

        var playlist = _services.Playlists.Create(name);
        NewPlaylistName = string.Empty;
        IsCreateFormOpen = false;
        _reportStatus($"已创建播放列表“{playlist.Name}”。");
    }

    /// <summary>返回卡片列表。</summary>
    [RelayCommand]
    private void BackToList()
    {
        SelectedPlaylist = null;
    }

    /// <summary>播放整个播放列表。</summary>
    [RelayCommand]
    private async Task PlayPlaylistAsync()
    {
        if (SelectedPlaylist is not { Model.Items.Count: > 0 } selected)
        {
            return;
        }

        var items = selected.Model.Items.ToArray();
        await _services.Playback.PlayAsync(items[0], items).ConfigureAwait(true);
    }

    /// <summary>打开播放列表详情。</summary>
    private void OpenPlaylist(PlaylistItemViewModel playlist)
    {
        SelectedPlaylist = playlist;
    }

    /// <summary>播放详情中的单个条目。</summary>
    private async Task PlayDetailItemAsync(MediaItemViewModel item)
    {
        if (SelectedPlaylist is null)
        {
            return;
        }

        var context = SelectedPlaylist.Model.Items.ToArray();
        await _services.Playback.PlayAsync(item.Item, context).ConfigureAwait(true);
    }

    /// <summary>从详情中移除条目。</summary>
    private void RemoveDetailItem(MediaItemViewModel item)
    {
        if (SelectedPlaylist is null)
        {
            return;
        }

        if (_services.Playlists.RemoveItem(SelectedPlaylist.Model, item.Item))
        {
            _reportStatus($"已从“{SelectedPlaylist.Name}”移除“{item.Title}”。");
        }
    }

    /// <summary>删除播放列表。</summary>
    private void DeletePlaylist(PlaylistItemViewModel playlist)
    {
        if (_services.Playlists.Delete(playlist.Model))
        {
            if (ReferenceEquals(SelectedPlaylist, playlist))
            {
                SelectedPlaylist = null;
            }

            _reportStatus($"已删除播放列表“{playlist.Name}”。");
        }
    }

    /// <summary>播放列表卡片上的播放命令。</summary>
    private async Task PlayListAsync(PlaylistItemViewModel playlist)
    {
        if (playlist.Model.Items.Count == 0)
        {
            _reportStatus("该播放列表为空。");
            return;
        }

        var items = playlist.Model.Items.ToArray();
        await _services.Playback.PlayAsync(items[0], items).ConfigureAwait(true);
    }

    private void Rebuild()
    {
        Playlists.Clear();
        foreach (var playlist in _services.Playlists.Playlists)
        {
            var viewModel = new PlaylistItemViewModel(
                playlist,
                OpenPlaylist,
                item => _ = PlayListAsync(item),
                DeletePlaylist);
            Playlists.Add(viewModel);
        }

        OnPropertyChanged(nameof(HasPlaylists));
        OnPropertyChanged(nameof(IsEmpty));
        if (SelectedPlaylist is not null)
        {
            var current = Playlists.FirstOrDefault(item =>
                string.Equals(item.Model.Id, SelectedPlaylist.Model.Id, StringComparison.Ordinal));
            if (current is null)
            {
                SelectedPlaylist = null;
            }
            else
            {
                SelectedPlaylist = current;
            }
        }
    }

    private void RebuildDetail()
    {
        DetailItems.Clear();
        if (SelectedPlaylist is not null)
        {
            foreach (var item in SelectedPlaylist.Model.Items)
            {
                DetailItems.Add(new MediaItemViewModel(
                    item,
                    _services.ArtworkLoader,
                    entry => _ = PlayDetailItemAsync(entry),
                    RemoveDetailItem));
            }
        }

        OnPropertyChanged(nameof(DetailSummary));
        OnPropertyChanged(nameof(IsDetailEmpty));
        OnPropertyChanged(nameof(HasDetailItems));
    }

    private void OnStoreChanged(object? sender, EventArgs e)
    {
        Rebuild();
        if (SelectedPlaylist is not null)
        {
            RebuildDetail();
        }
    }
}
