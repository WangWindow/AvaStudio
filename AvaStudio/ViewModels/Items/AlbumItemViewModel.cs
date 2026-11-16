using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AvaStudio.Helpers;
using AvaStudio.Models;
using AvaStudio.Services.Library;

namespace AvaStudio.ViewModels.Items;

/// <summary>专辑卡片的界面包装。</summary>
public sealed partial class AlbumItemViewModel : ViewModelBase
{
    private readonly ArtworkLoader _artworkLoader;
    private int _artworkVersion;

    public AlbumItemViewModel(
        AlbumGroup group,
        ArtworkLoader artworkLoader,
        Action<AlbumItemViewModel>? playAction,
        Action<AlbumItemViewModel>? openAction)
    {
        Group = group;
        _artworkLoader = artworkLoader;
        PlayCommand = new RelayCommand(() => playAction?.Invoke(this), () => playAction is not null);
        OpenCommand = new RelayCommand(() => openAction?.Invoke(this), () => openAction is not null);
    }

    /// <summary>聚合模型。</summary>
    public AlbumGroup Group { get; }

    /// <summary>专辑名。</summary>
    public string Name => Group.Name;

    /// <summary>专辑艺术家。</summary>
    public string Artist => Group.Artist;

    /// <summary>曲目数量文本。</summary>
    public string CountText => $"{Group.Count} 首歌曲";

    /// <summary>总时长文本。</summary>
    public string DurationText => DurationFormatter.FormatLong(Group.TotalDuration);

    /// <summary>曲目列表，供详情页使用。</summary>
    public IReadOnlyList<MediaItem> Items => Group.Items;

    /// <summary>封面位图。</summary>
    [ObservableProperty]
    public partial Bitmap? Artwork { get; set; }

    /// <summary>是否显示封面。</summary>
    public bool HasArtwork => Artwork is not null;

    /// <summary>播放整张专辑。</summary>
    public IRelayCommand PlayCommand { get; }

    /// <summary>打开专辑详情。</summary>
    public IRelayCommand OpenCommand { get; }

    /// <summary>加载专辑封面。</summary>
    public async Task LoadArtworkAsync(int width = ArtworkLoader.CardWidth)
    {
        if (Artwork is not null || string.IsNullOrWhiteSpace(Group.ArtworkPath))
        {
            return;
        }

        var version = _artworkVersion;
        var bitmap = await _artworkLoader.LoadAsync(Group.ArtworkPath, width).ConfigureAwait(true);
        if (bitmap is not null && version == _artworkVersion)
        {
            Artwork = bitmap;
        }
    }

    /// <summary>释放封面引用；离开音乐页时调用，避免封面缓存长期占用内存。</summary>
    public void ReleaseArtwork()
    {
        _artworkVersion++;
        Artwork = null;
    }

    partial void OnArtworkChanged(Bitmap? value)
    {
        OnPropertyChanged(nameof(HasArtwork));
    }
}
