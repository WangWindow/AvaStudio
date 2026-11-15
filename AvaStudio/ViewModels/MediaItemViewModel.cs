using System;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FluentIcons.Common;
using AvaStudio.Helpers;
using AvaStudio.Models;
using AvaStudio.Services.Library;

namespace AvaStudio.ViewModels;

/// <summary>
/// 单个媒体条目的界面包装。负责显示文本、封面加载、选中状态以及
/// 由页面注入的播放/移除命令；不直接持有服务或执行文件操作。
/// </summary>
public sealed partial class MediaItemViewModel : ViewModelBase
{
    private readonly Action<MediaItemViewModel>? _playAction;
    private readonly Action<MediaItemViewModel>? _removeAction;

    public MediaItemViewModel(
        MediaItem item,
        ArtworkLoader? artworkLoader = null,
        Action<MediaItemViewModel>? playAction = null,
        Action<MediaItemViewModel>? removeAction = null)
    {
        Item = item;
        _playAction = playAction;
        _removeAction = removeAction;
        _artworkLoader = artworkLoader;

        FileName = System.IO.Path.GetFileName(item.Path);
        if (string.IsNullOrWhiteSpace(FileName))
        {
            FileName = item.DisplayTitle;
        }

        DirectoryName = System.IO.Path.GetDirectoryName(item.Path) ?? string.Empty;
        TypeLabel = item.Kind == MediaItemKind.Video ? "视频" : "音频";
        KindSymbol = item.Kind == MediaItemKind.Video ? Symbol.Video : Symbol.MusicNote2;

        PlayCommand = new RelayCommand(Play, () => _playAction is not null);
        RemoveCommand = new RelayCommand(Remove, () => _removeAction is not null);
    }

    private readonly ArtworkLoader? _artworkLoader;

    /// <summary>原始媒体条目。</summary>
    public MediaItem Item { get; }

    /// <summary>文件名，用于队列/搜索结果。</summary>
    public string FileName { get; }

    /// <summary>所在目录，用于队列/搜索结果。</summary>
    public string DirectoryName { get; }

    /// <summary>媒体类型显示文本。</summary>
    public string TypeLabel { get; }

    /// <summary>媒体类型图标。</summary>
    public Symbol KindSymbol { get; }

    /// <summary>标题：优先标签标题。</summary>
    public string Title => Item.DisplayTitle;

    /// <summary>艺术家或“未知艺术家”。</summary>
    public string Artist => Item.DisplayArtist;

    /// <summary>专辑或“未知专辑”。</summary>
    public string Album => Item.DisplayAlbum;

    /// <summary>流派或“未知流派”。</summary>
    public string Genre => Item.DisplayGenre;

    /// <summary>歌曲行的副标题：艺术家 + 专辑；视频行使用目录名。</summary>
    public string Subtitle => Item.Kind == MediaItemKind.Video
        ? DirectoryName
        : Item.DisplayMeta;

    /// <summary>格式化后的时长。</summary>
    public string DurationText => DurationFormatter.Format(Item.Duration);

    /// <summary>完整路径，便于去重和调试。</summary>
    public string Path => Item.Path;

    /// <summary>是否具有可执行的移除命令。</summary>
    public bool HasRemoveAction => _removeAction is not null;

    /// <summary>是否具有可执行的播放命令。</summary>
    public bool HasPlayAction => _playAction is not null;

    /// <summary>封面位图；未加载时为 null。</summary>
    [ObservableProperty]
    public partial Bitmap? Artwork { get; set; }

    /// <summary>是否显示封面。</summary>
    public bool HasArtwork => Artwork is not null;

    /// <summary>是否为当前播放条目。</summary>
    [ObservableProperty]
    public partial bool IsCurrent { get; set; }

    /// <summary>复选框中是否选中。</summary>
    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    /// <summary>是否显示复选框（队列多选模式）。</summary>
    [ObservableProperty]
    public partial bool IsSelectionVisible { get; set; }

    /// <summary>播放该条目的命令。</summary>
    public IRelayCommand PlayCommand { get; }

    /// <summary>从队列或播放列表移除该条目的命令。</summary>
    public IRelayCommand RemoveCommand { get; }

    /// <summary>异步加载封面；重复调用会直接复用已加载的位图。</summary>
    public async Task LoadArtworkAsync(int width = ArtworkLoader.CardWidth)
    {
        if (Artwork is not null || _artworkLoader is null)
        {
            return;
        }

        var bitmap = await _artworkLoader.LoadAsync(Item.ArtworkPath, width).ConfigureAwait(true);
        if (bitmap is not null)
        {
            Artwork = bitmap;
        }
    }

    /// <summary>更新当前播放标记；由队列页面在切歌时调用。</summary>
    public void SetCurrent(bool isCurrent)
    {
        IsCurrent = isCurrent;
    }

    partial void OnArtworkChanged(Bitmap? value)
    {
        OnPropertyChanged(nameof(HasArtwork));
    }

    private void Play()
    {
        _playAction?.Invoke(this);
    }

    private void Remove()
    {
        _removeAction?.Invoke(this);
    }
}
