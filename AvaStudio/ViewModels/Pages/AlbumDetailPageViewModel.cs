using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FluentIcons.Common;
using AvaStudio.Helpers;
using AvaStudio.Models;
using AvaStudio.Services;
using AvaStudio.Services.Library;

namespace AvaStudio.ViewModels.Pages;

/// <summary>专辑详情页：封面、统计、整张播放与曲目列表。</summary>
public sealed partial class AlbumDetailPageViewModel : PageViewModelBase
{
    private readonly AppServices _services;
    private readonly Action _back;

    public AlbumDetailPageViewModel(
        AppServices services,
        AlbumGroup album,
        Action back)
        : base(album.Name, Symbol.Album)
    {
        _services = services;
        Album = album;
        _back = back;

        foreach (var item in album.Items)
        {
            Tracks.Add(new MediaItemViewModel(
                item,
                services.ArtworkLoader,
                track => _ = PlayTrackAsync(track)));
        }

        _ = LoadArtworkAsync();
    }

    /// <summary>专辑聚合。</summary>
    public AlbumGroup Album { get; }

    /// <summary>曲目列表。</summary>
    public ObservableCollection<MediaItemViewModel> Tracks { get; } = [];

    /// <summary>专辑名。</summary>
    public string AlbumName => Album.Name;

    /// <summary>专辑艺术家。</summary>
    public string ArtistName => Album.Artist;

    /// <summary>统计文本。</summary>
    public string SummaryText =>
        $"{Album.Count} 首歌曲 · {DurationFormatter.FormatLong(Album.TotalDuration)}";

    /// <summary>封面。</summary>
    [ObservableProperty]
    public partial Bitmap? Artwork { get; set; }

    /// <summary>是否显示封面。</summary>
    public bool HasArtwork => Artwork is not null;

    /// <summary>播放整张专辑。</summary>
    [RelayCommand]
    private async Task PlayAllAsync()
    {
        if (Album.Items.Count > 0)
        {
            await _services.Playback.PlayAsync(Album.Items[0], Album.Items).ConfigureAwait(true);
        }
    }

    /// <summary>随机播放整张专辑。</summary>
    [RelayCommand]
    private async Task ShuffleAsync()
    {
        if (Album.Items.Count == 0)
        {
            return;
        }

        var items = Album.Items.OrderBy(_ => Random.Shared.Next()).ToArray();
        await _services.Playback.PlayAsync(items[0], items).ConfigureAwait(true);
    }

    /// <summary>返回音乐页。</summary>
    [RelayCommand]
    private void GoBack() => _back();

    /// <summary>播放单曲。</summary>
    private async Task PlayTrackAsync(MediaItemViewModel track)
    {
        var context = Tracks.Select(static item => item.Item).ToArray();
        await _services.Playback.PlayAsync(track.Item, context).ConfigureAwait(true);
    }

    private async Task LoadArtworkAsync()
    {
        if (string.IsNullOrWhiteSpace(Album.ArtworkPath))
        {
            return;
        }

        Artwork = await _services.ArtworkLoader
            .LoadAsync(Album.ArtworkPath, ArtworkLoader.CardWidth)
            .ConfigureAwait(true);
    }

    partial void OnArtworkChanged(Bitmap? value)
    {
        OnPropertyChanged(nameof(HasArtwork));
    }
}
