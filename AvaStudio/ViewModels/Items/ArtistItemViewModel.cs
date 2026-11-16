using System;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AvaStudio.Models;
using AvaStudio.Services.Library;

namespace AvaStudio.ViewModels.Items;

/// <summary>艺术家卡片的界面包装。</summary>
public sealed partial class ArtistItemViewModel : ViewModelBase
{
    private readonly ArtworkLoader _artworkLoader;
    private int _artworkVersion;

    public ArtistItemViewModel(
        ArtistGroup group,
        ArtworkLoader artworkLoader,
        Action<ArtistItemViewModel>? playAction,
        Action<ArtistItemViewModel>? openAction)
    {
        Group = group;
        _artworkLoader = artworkLoader;
        PlayCommand = new RelayCommand(() => playAction?.Invoke(this), () => playAction is not null);
        OpenCommand = new RelayCommand(() => openAction?.Invoke(this), () => openAction is not null);
    }

    /// <summary>聚合模型。</summary>
    public ArtistGroup Group { get; }

    /// <summary>艺术家名。</summary>
    public string Name => Group.Name;

    /// <summary>统计文本：专辑数与歌曲数。</summary>
    public string SummaryText => $"{Group.AlbumCount} 张专辑 · {Group.Count} 首歌曲";

    /// <summary>封面位图。</summary>
    [ObservableProperty]
    public partial Bitmap? Artwork { get; set; }

    /// <summary>是否显示封面。</summary>
    public bool HasArtwork => Artwork is not null;

    /// <summary>播放该艺术家的全部歌曲。</summary>
    public IRelayCommand PlayCommand { get; }

    /// <summary>打开艺术家详情。</summary>
    public IRelayCommand OpenCommand { get; }

    /// <summary>加载艺术家头像。</summary>
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
