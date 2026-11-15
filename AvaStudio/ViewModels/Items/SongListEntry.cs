using AvaStudio.ViewModels;

namespace AvaStudio.ViewModels.Items;

/// <summary>
/// 歌曲列表的扁平条目：字母表头或歌曲行。
/// 扁平化后可以交给 VirtualizingStackPanel，只创建视口内的行，
/// 避免首次进入音乐页时一次性布局 300+ 行造成卡顿。
/// </summary>
public sealed class SongListEntry
{
    private SongListEntry(string? header, MediaItemViewModel? song, bool isAlternate)
    {
        Header = header;
        Song = song;
        IsAlternate = isAlternate;
    }

    /// <summary>字母分组标题；歌曲行为 null。</summary>
    public string? Header { get; }

    /// <summary>歌曲行数据；表头行为 null。</summary>
    public MediaItemViewModel? Song { get; }

    /// <summary>是否为字母表头。</summary>
    public bool IsHeader => Song is null;

    /// <summary>是否为交替行；表头行始终为 false。</summary>
    public bool IsAlternate { get; }

    public static SongListEntry FromHeader(string header) => new(header, null, isAlternate: false);

    public static SongListEntry FromSong(MediaItemViewModel song, bool isAlternate) =>
        new(null, song, isAlternate);
}
