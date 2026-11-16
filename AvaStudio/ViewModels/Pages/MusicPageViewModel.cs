using System;
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

/// <summary>音乐页的三个分区。</summary>
public enum MusicSection
{
    Songs,
    Albums,
    Artists,
}

/// <summary>
/// 音乐页：歌曲列表、专辑网格与艺术家网格。数据来自 <see cref="LibraryService"/> 快照；
/// 打开专辑/艺术家详情通过回调交给主 ViewModel 导航。
/// </summary>
public sealed partial class MusicPageViewModel : PageViewModelBase
{
    private readonly AppServices _services;

    private readonly Action<string> _reportStatus;
    private readonly Action<AlbumGroup> _openAlbum;
    private readonly Action<ArtistGroup> _openArtist;
    private readonly List<MediaItemViewModel> _allSongs = [];
    private bool _contentBuilt;
    private bool _isActive;

    public MusicPageViewModel(
        AppServices services,
        Action<AlbumGroup> openAlbum,
        Action<ArtistGroup> openArtist,
        Action<string> reportStatus)
        : base("音乐", Symbol.MusicNote2)
    {
        _services = services;
        _openAlbum = openAlbum;
        _openArtist = openArtist;
        _reportStatus = reportStatus;
        _services.Library.Changed += OnLibraryChanged;
        _services.Settings.Changed += OnSettingsChanged;
    }

    /// <summary>按当前排序筛选后的歌曲。</summary>
    public ObservableCollection<MediaItemViewModel> Songs { get; } = [];

    /// <summary>扁平化的歌曲行与字母表头；供虚拟化列表按视口创建。</summary>
    public ObservableCollection<SongListEntry> SongEntries { get; } = [];

    /// <summary>排序选项显示文本。</summary>
    public IReadOnlyList<string> SortOptions { get; } = ["标题", "艺术家", "专辑"];

    /// <summary>全部专辑。</summary>
    public ObservableCollection<AlbumItemViewModel> Albums { get; } = [];

    /// <summary>专辑字母分组。</summary>
    public ObservableCollection<AlbumLetterGroup> AlbumGroups { get; } = [];

    /// <summary>全部艺术家。</summary>
    public ObservableCollection<ArtistItemViewModel> Artists { get; } = [];

    /// <summary>艺术家字母分组。</summary>
    public ObservableCollection<ArtistLetterGroup> ArtistGroups { get; } = [];

    /// <summary>当前分区。</summary>
    [ObservableProperty]
    public partial MusicSection SelectedSection { get; set; } = MusicSection.Songs;

    /// <summary>当前排序索引：0 标题、1 艺术家、2 专辑。</summary>
    [ObservableProperty]
    public partial int SelectedSortIndex { get; set; }

    /// <summary>是否降序；默认升序。</summary>
    [ObservableProperty]
    public partial bool IsSortDescending { get; set; }

    /// <summary>排序方向图标：单箭头突出当前升/降序。</summary>
    public Symbol SortSymbol => IsSortDescending ? Symbol.ArrowSortDown : Symbol.ArrowSortUp;

    /// <summary>排序方向提示。</summary>
    public string SortDirectionTip => IsSortDescending ? "当前降序，点击切换为升序" : "当前升序，点击切换为降序";

    /// <summary>是否选中歌曲分区。</summary>
    public bool IsSongsSelected => SelectedSection == MusicSection.Songs;

    /// <summary>是否选中专辑分区。</summary>
    public bool IsAlbumsSelected => SelectedSection == MusicSection.Albums;

    /// <summary>是否选中艺术家分区。</summary>
    public bool IsArtistsSelected => SelectedSection == MusicSection.Artists;

    /// <summary>资料库音乐文件夹数量。</summary>
    public int MusicLocationCount => _services.Settings.MusicFolders.Count;

    /// <summary>是否有资料库位置。</summary>
    public bool HasLibraryLocations => MusicLocationCount > 0;

    /// <summary>资料库位置摘要。</summary>
    public string LocationSummary => MusicLocationCount == 0
        ? "尚未添加音乐文件夹"
        : $"已指定 {MusicLocationCount} 个资料库位置";

    /// <summary>是否有歌曲。</summary>
    public bool HasSongs => Songs.Count > 0;

    /// <summary>是否有专辑。</summary>
    public bool HasAlbums => Albums.Count > 0;

    /// <summary>是否有艺术家。</summary>
    public bool HasArtists => Artists.Count > 0;

    /// <summary>当前分区是否有内容。</summary>
    public bool HasContent => SelectedSection switch
    {
        MusicSection.Albums => Albums.Count > 0,
        MusicSection.Artists => Artists.Count > 0,
        _ => Songs.Count > 0,
    };

    /// <summary>是否显示空状态。</summary>
    public bool IsEmpty => !HasContent;

    /// <summary>空状态标题。</summary>
    public string EmptyTitle => SelectedSection switch
    {
        MusicSection.Albums => "还没有专辑",
        MusicSection.Artists => "还没有艺术家",
        _ => "还没有歌曲",
    };

    /// <summary>空状态说明。</summary>
    public string EmptyDescription => HasLibraryLocations
        ? "正在等待资料库扫描完成，或所选文件夹中没有音频文件。"
        : "添加音乐文件夹后会扫描其中的音频标签并在本页显示。";

    /// <summary>空状态图标。</summary>
    public Symbol SectionSymbol => SelectedSection switch
    {
        MusicSection.Albums => Symbol.Album,
        MusicSection.Artists => Symbol.People,
        _ => Symbol.MusicNote2,
    };

    /// <summary>是否可以随机播放。</summary>
    public bool CanRandomPlay => Songs.Count > 0;

    /// <summary>随机播放提示。</summary>
    public string RandomPlayHint => CanRandomPlay
        ? "随机播放资料库中的歌曲"
        : "资料库中还没有可播放的歌曲";

    /// <summary>歌曲分区显示文本。</summary>
    public string SongCountText => $"{Songs.Count} 首歌曲";

    /// <summary>专辑分区显示文本。</summary>
    public string AlbumCountText => $"{Albums.Count} 张专辑";

    /// <summary>艺术家分区显示文本。</summary>
    public string ArtistCountText => $"{Artists.Count} 位艺术家";

    partial void OnSelectedSectionChanged(MusicSection value)
    {
        RaiseSectionProperties();
        ReleaseInactiveSectionArtwork(value);
        EnsureSectionArtwork();
    }

    /// <summary>切换分区时释放另一个分区的封面，避免专辑和艺术家封面同时常驻。</summary>
    private void ReleaseInactiveSectionArtwork(MusicSection activeSection)
    {
        if (activeSection != MusicSection.Albums)
        {
            foreach (var album in Albums)
            {
                album.ReleaseArtwork();
            }
        }

        if (activeSection != MusicSection.Artists)
        {
            foreach (var artist in Artists)
            {
                artist.ReleaseArtwork();
            }
        }

        _services.ArtworkLoader.Clear();
    }

    /// <summary>
    /// 进入音乐页时才构建歌曲/专辑/艺术家视图模型；离开时整体释放。
    /// 专辑和艺术家网格即使虚拟化，几百个卡片 ViewModel 也会长期占用内存。
    /// </summary>
    public override void OnNavigatedTo()
    {
        _isActive = true;
        if (!_contentBuilt)
        {
            Rebuild();
        }

        EnsureSectionArtwork();
    }

    /// <summary>离开音乐页时释放封面、分组和全部卡片视图模型。</summary>
    public override void OnNavigatedFrom()
    {
        _isActive = false;
        ReleaseArtwork();
        ReleaseContent();
    }

    /// <summary>释放封面引用，清空全局图片缓存引用，等待 GC 回收。</summary>
    private void ReleaseArtwork()
    {
        foreach (var album in Albums)
        {
            album.ReleaseArtwork();
        }

        foreach (var artist in Artists)
        {
            artist.ReleaseArtwork();
        }

        _services.ArtworkLoader.Clear();
    }

    /// <summary>清空三个分区的视图模型和分组；返回音乐页时由快照重新构建。</summary>
    private void ReleaseContent()
    {
        _allSongs.Clear();
        Songs.Clear();
        SongEntries.Clear();
        Albums.Clear();
        AlbumGroups.Clear();
        Artists.Clear();
        ArtistGroups.Clear();
        _contentBuilt = false;
    }

    /// <summary>按当前分区懒加载封面；切回已加载的分区时不会重复解码。</summary>
    private void EnsureSectionArtwork()
    {
        if (SelectedSection == MusicSection.Albums)
        {
            foreach (var album in Albums)
            {
                _ = album.LoadArtworkAsync();
            }
        }
        else if (SelectedSection == MusicSection.Artists)
        {
            foreach (var artist in Artists)
            {
                _ = artist.LoadArtworkAsync();
            }
        }
    }

    partial void OnSelectedSortIndexChanged(int value)
    {
        ApplySongView();
    }

    partial void OnIsSortDescendingChanged(bool value)
    {
        ApplySongView();
        OnPropertyChanged(nameof(SortSymbol));
        OnPropertyChanged(nameof(SortDirectionTip));
    }

    /// <summary>切换歌曲排序的升序/降序。</summary>
    [RelayCommand]
    private void ToggleSortDirection() => IsSortDescending = !IsSortDescending;

    /// <summary>切换到歌曲分区。</summary>
    [RelayCommand]
    private void SelectSongs() => SelectedSection = MusicSection.Songs;

    /// <summary>切换到专辑分区。</summary>
    [RelayCommand]
    private void SelectAlbums() => SelectedSection = MusicSection.Albums;

    /// <summary>切换到艺术家分区。</summary>
    [RelayCommand]
    private void SelectArtists() => SelectedSection = MusicSection.Artists;

    /// <summary>随机播放全部歌曲。</summary>
    [RelayCommand(CanExecute = nameof(CanRandomPlay))]
    private async Task RandomPlayAsync()
    {
        if (Songs.Count == 0)
        {
            return;
        }

        var items = Songs.Select(static song => song.Item).ToArray();
        var index = Random.Shared.Next(items.Length);
        await _services.Playback.PlayAsync(items[index], items).ConfigureAwait(true);
    }

    /// <summary>添加音乐文件夹并触发扫描。</summary>
    [RelayCommand]
    private async Task AddMusicFolderAsync()
    {
        var folder = await _services.Desktop.PickFolderAsync("添加音乐文件夹").ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(folder))
        {
            return;
        }

        var result = _services.Settings.AddMusicFolder(folder);
        _reportStatus(DescribeAddResult(result));
        if (result == SettingsAddResult.Added)
        {
            await _services.Library.RefreshAsync().ConfigureAwait(true);
        }
    }

    /// <summary>播放单首歌曲，以当前歌曲列表为队列。</summary>
    private async Task PlaySongAsync(MediaItemViewModel song)
    {
        var context = Songs.Select(static entry => entry.Item).ToArray();
        await _services.Playback.PlayAsync(song.Item, context).ConfigureAwait(true);
    }

    /// <summary>播放整张专辑。</summary>
    private async Task PlayAlbumAsync(AlbumItemViewModel album)
    {
        if (album.Items.Count == 0)
        {
            return;
        }

        await _services.Playback.PlayAsync(album.Items[0], album.Items).ConfigureAwait(true);
    }

    /// <summary>播放艺术家的全部歌曲。</summary>
    private async Task PlayArtistAsync(ArtistItemViewModel artist)
    {
        if (artist.Group.Items.Count == 0)
        {
            return;
        }

        await _services.Playback.PlayAsync(artist.Group.Items[0], artist.Group.Items).ConfigureAwait(true);
    }

    /// <summary>根据资料库快照重建三个分区。</summary>
    private void Rebuild()
    {
        var snapshot = _services.Library.Snapshot;

        _allSongs.Clear();
        foreach (var item in snapshot.MusicItems)
        {
            _allSongs.Add(new MediaItemViewModel(
                item,
                _services.ArtworkLoader,
                song => _ = PlaySongAsync(song)));
        }

        ApplySongView();

        Albums.Clear();
        foreach (var viewModel in snapshot.Albums
                     .Select(group => new AlbumItemViewModel(
                         group,
                         _services.ArtworkLoader,
                         album => _ = PlayAlbumAsync(album),
                         album => _openAlbum(album.Group)))
                     .OrderBy(static album => LibraryTextSort.GetSortKey(album.Name), StringComparer.OrdinalIgnoreCase)
                     .ThenBy(static album => album.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            Albums.Add(viewModel);
        }

        Artists.Clear();
        foreach (var viewModel in snapshot.Artists
                     .Select(group => new ArtistItemViewModel(
                         group,
                         _services.ArtworkLoader,
                         artist => _ = PlayArtistAsync(artist),
                         artist => _openArtist(artist.Group)))
                     .OrderBy(static artist => LibraryTextSort.GetSortKey(artist.Name), StringComparer.OrdinalIgnoreCase)
                     .ThenBy(static artist => artist.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            Artists.Add(viewModel);
        }

        BuildLibraryGroups();
        RaiseSectionProperties();
        RandomPlayCommand.NotifyCanExecuteChanged();
        _contentBuilt = true;
    }

    /// <summary>按首字母构建专辑与艺术家分组，保持与歌曲列表一致的分组顺序。</summary>
    private void BuildLibraryGroups()
    {
        AlbumGroups.Clear();
        foreach (var group in Albums
                     .GroupBy(album => LibraryTextSort.GetLetterHeader(album.Name))
                     .OrderBy(static group => GetHeaderOrder(group.Key))
                     .ThenBy(static group => group.Key, StringComparer.CurrentCultureIgnoreCase))
        {
            var letterGroup = new AlbumLetterGroup(group.Key);
            foreach (var album in group)
            {
                letterGroup.Albums.Add(album);
            }

            AlbumGroups.Add(letterGroup);
        }

        ArtistGroups.Clear();
        foreach (var group in Artists
                     .GroupBy(artist => LibraryTextSort.GetLetterHeader(artist.Name))
                     .OrderBy(static group => GetHeaderOrder(group.Key))
                     .ThenBy(static group => group.Key, StringComparer.CurrentCultureIgnoreCase))
        {
            var letterGroup = new ArtistLetterGroup(group.Key);
            foreach (var artist in group)
            {
                letterGroup.Artists.Add(artist);
            }

            ArtistGroups.Add(letterGroup);
        }
    }

    /// <summary>应用排序并重新生成字母分组。</summary>
    private void ApplySongView()
    {
        IEnumerable<MediaItemViewModel> query = _allSongs;

        query = SelectedSortIndex switch
        {
            1 => IsSortDescending
                ? query.OrderByDescending(static song => LibraryTextSort.GetSortKey(song.Artist), StringComparer.OrdinalIgnoreCase)
                    .ThenByDescending(static song => song.Artist, StringComparer.CurrentCultureIgnoreCase)
                    .ThenByDescending(static song => song.Title, StringComparer.CurrentCultureIgnoreCase)
                : query.OrderBy(static song => LibraryTextSort.GetSortKey(song.Artist), StringComparer.OrdinalIgnoreCase)
                    .ThenBy(static song => song.Artist, StringComparer.CurrentCultureIgnoreCase)
                    .ThenBy(static song => song.Title, StringComparer.CurrentCultureIgnoreCase),
            2 => IsSortDescending
                ? query.OrderByDescending(static song => LibraryTextSort.GetSortKey(song.Album), StringComparer.OrdinalIgnoreCase)
                    .ThenByDescending(static song => song.Album, StringComparer.CurrentCultureIgnoreCase)
                    .ThenByDescending(static song => song.Title, StringComparer.CurrentCultureIgnoreCase)
                : query.OrderBy(static song => LibraryTextSort.GetSortKey(song.Album), StringComparer.OrdinalIgnoreCase)
                    .ThenBy(static song => song.Album, StringComparer.CurrentCultureIgnoreCase)
                    .ThenBy(static song => song.Title, StringComparer.CurrentCultureIgnoreCase),
            _ => IsSortDescending
                ? query.OrderByDescending(static song => LibraryTextSort.GetSortKey(song.Title), StringComparer.OrdinalIgnoreCase)
                    .ThenByDescending(static song => song.Title, StringComparer.CurrentCultureIgnoreCase)
                : query.OrderBy(static song => LibraryTextSort.GetSortKey(song.Title), StringComparer.OrdinalIgnoreCase)
                    .ThenBy(static song => song.Title, StringComparer.CurrentCultureIgnoreCase),
        };

        Songs.Clear();
        foreach (var song in query)
        {
            Songs.Add(song);
        }

        SongEntries.Clear();
        var rowIndex = 0;
        foreach (var group in Songs.GroupBy(song => LibraryTextSort.GetLetterHeader(song.Title))
                     .OrderBy(static group => GetHeaderOrder(group.Key))
                     .ThenBy(static group => group.Key, StringComparer.CurrentCultureIgnoreCase))
        {
            SongEntries.Add(SongListEntry.FromHeader(group.Key));
            foreach (var song in group)
            {
                SongEntries.Add(SongListEntry.FromSong(song, rowIndex++ % 2 == 0));
            }
        }

        RaiseSectionProperties();
        RandomPlayCommand.NotifyCanExecuteChanged();
    }

    /// <summary>分组排序：&amp; → # → A-Z。</summary>
    private static int GetHeaderOrder(string header) => header switch
    {
        "&" => 0,
        "#" => 1,
        _ => 2,
    };

    /// <summary>通知所有分区相关属性。</summary>
    private void RaiseSectionProperties()
    {
        OnPropertyChanged(nameof(IsSongsSelected));
        OnPropertyChanged(nameof(IsAlbumsSelected));
        OnPropertyChanged(nameof(IsArtistsSelected));
        OnPropertyChanged(nameof(HasSongs));
        OnPropertyChanged(nameof(HasAlbums));
        OnPropertyChanged(nameof(SongEntries));
        OnPropertyChanged(nameof(AlbumGroups));
        OnPropertyChanged(nameof(ArtistGroups));
        OnPropertyChanged(nameof(HasArtists));
        OnPropertyChanged(nameof(HasContent));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(EmptyTitle));
        OnPropertyChanged(nameof(EmptyDescription));
        OnPropertyChanged(nameof(SectionSymbol));
        OnPropertyChanged(nameof(CanRandomPlay));
        OnPropertyChanged(nameof(RandomPlayHint));
        OnPropertyChanged(nameof(SongCountText));
        OnPropertyChanged(nameof(AlbumCountText));
        OnPropertyChanged(nameof(ArtistCountText));
        OnPropertyChanged(nameof(MusicLocationCount));
        OnPropertyChanged(nameof(HasLibraryLocations));
        OnPropertyChanged(nameof(LocationSummary));
    }

    private void OnLibraryChanged(object? sender, EventArgs e)
    {
        // 页面未显示时不重建；下次进入会按最新快照构建。
        if (_isActive)
        {
            Rebuild();
        }
        else
        {
            _contentBuilt = false;
        }
    }

    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(MusicLocationCount));
        OnPropertyChanged(nameof(HasLibraryLocations));
        OnPropertyChanged(nameof(LocationSummary));
    }

    private static string DescribeAddResult(SettingsAddResult result) => result switch
    {
        SettingsAddResult.Added => "已添加音乐资料库位置，正在扫描…",
        SettingsAddResult.Duplicate => "该文件夹已在音乐资料库中。",
        SettingsAddResult.SaveFailed => "无法保存资料库位置，请检查本地设置目录。",
        _ => "无法使用该文件夹位置。",
    };
}
