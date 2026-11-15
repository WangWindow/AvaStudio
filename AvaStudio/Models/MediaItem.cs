using System.Text.Json.Serialization;

namespace AvaStudio.Models;

/// <summary>媒体条目的类型。</summary>
public enum MediaItemKind
{
    /// <summary>音频文件。</summary>
    Audio,

    /// <summary>视频文件。</summary>
    Video,
}

/// <summary>
/// 资料库中一个媒体文件的不可变元数据快照。
/// 该对象可以安全地在后台线程创建，并直接交给界面绑定；
/// 路径是唯一稳定键，其余字段均来自标签读取或文件名回退。
/// </summary>
public sealed record MediaItem
{
    /// <summary>规范化后的绝对路径，也是资料库与队列的稳定键。</summary>
    public required string Path { get; init; }

    /// <summary>音频或视频。</summary>
    public MediaItemKind Kind { get; init; }

    /// <summary>标题；缺失时由文件名回退。</summary>
    public string Title { get; init; } = string.Empty;

    /// <summary>艺术家；缺失时为“未知艺术家”。</summary>
    public string Artist { get; init; } = string.Empty;

    /// <summary>专辑名；缺失时为“未知专辑”。</summary>
    public string Album { get; init; } = string.Empty;

    /// <summary>流派；缺失时为空字符串。</summary>
    public string Genre { get; init; } = string.Empty;

    /// <summary>专辑艺术家；用于专辑聚合，缺失时回退到 <see cref="Artist"/>。</summary>
    public string AlbumArtist { get; init; } = string.Empty;

    /// <summary>音轨序号；未知时为 null。</summary>
    public uint? TrackNumber { get; init; }

    /// <summary>发行年份；未知时为 null。</summary>
    public uint? Year { get; init; }

    /// <summary>时长；无法读取时为 <see cref="TimeSpan.Zero"/>。</summary>
    public TimeSpan Duration { get; init; }

    /// <summary>提取到本机缓存中的封面路径；没有封面时为 null。</summary>
    public string? ArtworkPath { get; init; }

    /// <summary>文件大小（字节），用于检测文件是否变化。</summary>
    public long FileSize { get; init; }

    /// <summary>文件最后修改时间（UTC ticks），用于检测文件是否变化。</summary>
    public long FileModifiedTicks { get; init; }

    /// <summary>首次加入资料库的时间。</summary>
    public DateTimeOffset AddedUtc { get; init; }

    /// <summary>最近一次播放时间；从未播放时为 null。</summary>
    public DateTimeOffset? LastPlayedUtc { get; init; }

    /// <summary>用于界面显示的标题：优先标签标题，其次文件名。</summary>
    [JsonIgnore]
    public string DisplayTitle
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Title))
            {
                return Title;
            }

            var fileName = System.IO.Path.GetFileNameWithoutExtension(Path);
            return string.IsNullOrWhiteSpace(fileName) ? Path : fileName;
        }
    }

    /// <summary>用于界面显示的艺术家，保证非空。</summary>
    [JsonIgnore]
    public string DisplayArtist => string.IsNullOrWhiteSpace(Artist) ? "未知艺术家" : Artist;

    /// <summary>用于界面显示的专辑名，保证非空。</summary>
    [JsonIgnore]
    public string DisplayAlbum => string.IsNullOrWhiteSpace(Album) ? "未知专辑" : Album;

    /// <summary>用于界面显示的流派，保证非空。</summary>
    [JsonIgnore]
    public string DisplayGenre => string.IsNullOrWhiteSpace(Genre) ? "未知流派" : Genre;

    /// <summary>用于界面显示的专辑艺术家，保证非空。</summary>
    [JsonIgnore]
    public string DisplayAlbumArtist =>
        string.IsNullOrWhiteSpace(AlbumArtist)
            ? DisplayArtist
            : AlbumArtist;

    /// <summary>用于播放器副标题的“艺术家 · 专辑”文本。</summary>
    [JsonIgnore]
    public string DisplayMeta =>
        string.IsNullOrWhiteSpace(Album)
            ? DisplayArtist
            : $"{DisplayArtist} · {DisplayAlbum}";

    /// <summary>返回一个只更新最近播放时间的新快照。</summary>
    public MediaItem WithLastPlayed(DateTimeOffset playedUtc) =>
        this with { LastPlayedUtc = playedUtc };

    /// <summary>返回一个只更新封面路径的新快照。</summary>
    public MediaItem WithArtwork(string? artworkPath) =>
        this with { ArtworkPath = artworkPath };
}
