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
using AvaStudio.ViewModels.Items;

namespace AvaStudio.ViewModels.Pages;

/// <summary>艺术家详情页：头像、统计、专辑网格与全部歌曲。</summary>
public sealed partial class ArtistDetailPageViewModel : PageViewModelBase
{
    private readonly AppServices _services;
    private readonly Action _back;
    private readonly Action<AlbumGroup> _openAlbum;

    public ArtistDetailPageViewModel(
        AppServices services,
        ArtistGroup artist,
        Action back,
        Action<AlbumGroup> openAlbum)
        : base(artist.Name, Symbol.People)
    {
        _services = services;
        Artist = artist;
        _back = back;
        _openAlbum = openAlbum;

        foreach (var album in artist.Albums)
        {
            var viewModel = new AlbumItemViewModel(
                album,
                services.ArtworkLoader,
                item => _ = PlayAlbumAsync(item),
                item => _openAlbum(item.Group));
            _ = viewModel.LoadArtworkAsync();
            Albums.Add(viewModel);
        }

        foreach (var item in artist.Items)
        {
            Tracks.Add(new MediaItemViewModel(
                item,
                services.ArtworkLoader,
                track => _ = PlayTrackAsync(track)));
        }

        _ = LoadArtworkAsync();
    }

    /// <summary>艺术家聚合。</summary>
    public ArtistGroup Artist { get; }

    /// <summary>专辑网格。</summary>
    public ObservableCollection<AlbumItemViewModel> Albums { get; } = [];

    /// <summary>歌曲列表。</summary>
    public ObservableCollection<MediaItemViewModel> Tracks { get; } = [];

    /// <summary>艺术家名。</summary>
    public string ArtistName => Artist.Name;

    /// <summary>统计文本。</summary>
    public string SummaryText =>
        $"{Artist.AlbumCount} 张专辑 · {Artist.Count} 首歌曲 · {DurationFormatter.FormatLong(Artist.TotalDuration)}";

    /// <summary>是否有专辑。</summary>
    public bool HasAlbums => Albums.Count > 0;

    /// <summary>头像。</summary>
    [ObservableProperty]
    public partial Bitmap? Artwork { get; set; }

    /// <summary>是否显示头像。</summary>
    public bool HasArtwork => Artwork is not null;

    /// <summary>播放艺术家全部歌曲。</summary>
    [RelayCommand]
    private async Task PlayAllAsync()
    {
        if (Artist.Items.Count > 0)
        {
            await _services.Playback.PlayAsync(Artist.Items[0], Artist.Items).ConfigureAwait(true);
        }
    }

    /// <summary>随机播放艺术家全部歌曲。</summary>
    [RelayCommand]
    private async Task ShuffleAsync()
    {
        if (Artist.Items.Count == 0)
        {
            return;
        }

        var items = Artist.Items.OrderBy(_ => Random.Shared.Next()).ToArray();
        await _services.Playback.PlayAsync(items[0], items).ConfigureAwait(true);
    }

    /// <summary>返回音乐页。</summary>
    [RelayCommand]
    private void GoBack() => _back();

    private async Task PlayAlbumAsync(AlbumItemViewModel album)
    {
        if (album.Items.Count > 0)
        {
            await _services.Playback.PlayAsync(album.Items[0], album.Items).ConfigureAwait(true);
        }
    }

    private async Task PlayTrackAsync(MediaItemViewModel track)
    {
        var context = Tracks.Select(static item => item.Item).ToArray();
        await _services.Playback.PlayAsync(track.Item, context).ConfigureAwait(true);
    }

    private async Task LoadArtworkAsync()
    {
        if (string.IsNullOrWhiteSpace(Artist.ArtworkPath))
        {
            return;
        }

        Artwork = await _services.ArtworkLoader
            .LoadAsync(Artist.ArtworkPath, ArtworkLoader.CardWidth)
            .ConfigureAwait(true);
    }

    partial void OnArtworkChanged(Bitmap? value)
    {
        OnPropertyChanged(nameof(HasArtwork));
    }
}
