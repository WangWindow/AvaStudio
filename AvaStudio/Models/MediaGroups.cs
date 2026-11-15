namespace AvaStudio.Models;

/// <summary>按专辑聚合后的展示模型。</summary>
public sealed class AlbumGroup(string key, string name, string artist, string? artworkPath, IReadOnlyList<MediaItem> items)
{
    /// <summary>专辑聚合键（专辑名 + 专辑艺术家）。</summary>
    public string Key { get; } = key;

    /// <summary>专辑名，保证非空。</summary>
    public string Name { get; } = name;

    /// <summary>专辑艺术家，保证非空。</summary>
    public string Artist { get; } = artist;

    /// <summary>封面路径；优先取专辑内第一张有封面的曲目。</summary>
    public string? ArtworkPath { get; } = artworkPath;

    /// <summary>专辑内曲目，按音轨号与标题排序。</summary>
    public IReadOnlyList<MediaItem> Items { get; } = items;

    /// <summary>专辑总时长。</summary>
    public TimeSpan TotalDuration => Items.Aggregate(TimeSpan.Zero, static (sum, item) => sum + item.Duration);

    /// <summary>曲目数量。</summary>
    public int Count => Items.Count;
}

/// <summary>按艺术家聚合后的展示模型。</summary>
public sealed class ArtistGroup(string name, IReadOnlyList<AlbumGroup> albums, IReadOnlyList<MediaItem> items)
{
    /// <summary>艺术家名，保证非空。</summary>
    public string Name { get; } = name;

    /// <summary>该艺术家的专辑。</summary>
    public IReadOnlyList<AlbumGroup> Albums { get; } = albums;

    /// <summary>该艺术家的全部音频曲目。</summary>
    public IReadOnlyList<MediaItem> Items { get; } = items;

    /// <summary>用于圆形头像的封面路径。</summary>
    public string? ArtworkPath => Albums.Select(static album => album.ArtworkPath).FirstOrDefault(static path => path is not null);

    /// <summary>曲目数量。</summary>
    public int Count => Items.Count;

    /// <summary>专辑数量。</summary>
    public int AlbumCount => Albums.Count;

    /// <summary>总时长。</summary>
    public TimeSpan TotalDuration => Items.Aggregate(TimeSpan.Zero, static (sum, item) => sum + item.Duration);
}

/// <summary>
/// 视频资料库中的目录节点。与按“有视频的目录”聚合的 <see cref="VideoFolderGroup"/> 不同，
/// 该节点保留空子目录，并区分当前目录的直接视频与子目录，供文件夹浏览页使用。
/// </summary>
public sealed class VideoDirectoryNode
{
    public VideoDirectoryNode(
        string path,
        IReadOnlyList<VideoDirectoryNode> children,
        IReadOnlyList<MediaItem> videos)
    {
        Path = path;
        Children = children;
        Videos = videos;
    }

    /// <summary>目录绝对路径。</summary>
    public string Path { get; }

    /// <summary>父目录路径；资料库根目录的父目录可能不在资料库内。</summary>
    public string? ParentPath => System.IO.Path.GetDirectoryName(Path);

    /// <summary>目录显示名。</summary>
    public string Name
    {
        get
        {
            var trimmed = System.IO.Path.TrimEndingDirectorySeparator(Path);
            var name = System.IO.Path.GetFileName(trimmed);
            return string.IsNullOrWhiteSpace(name) ? Path : name;
        }
    }

    /// <summary>直接子目录，按名称排序。</summary>
    public IReadOnlyList<VideoDirectoryNode> Children { get; }

    /// <summary>当前目录下的直接视频。</summary>
    public IReadOnlyList<MediaItem> Videos { get; }

    /// <summary>直接视频数量。</summary>
    public int VideoCount => Videos.Count;

    /// <summary>递归视频总数。</summary>
    public int TotalVideoCount => Videos.Count + Children.Sum(static child => child.TotalVideoCount);

    /// <summary>目录项总数（直接视频 + 子目录）。</summary>
    public int ItemCount => Videos.Count + Children.Count;

    /// <summary>是否为空目录。</summary>
    public bool IsEmpty => Videos.Count == 0 && Children.Count == 0;
}

/// <summary>按目录聚合的视频文件夹。</summary>
public sealed class VideoFolderGroup(string path, IReadOnlyList<MediaItem> items)
{
    /// <summary>目录绝对路径。</summary>
    public string Path { get; } = path;

    /// <summary>文件夹显示名。</summary>
    public string Name
    {
        get
        {
            var trimmed = System.IO.Path.TrimEndingDirectorySeparator(Path);
            var name = System.IO.Path.GetFileName(trimmed);
            return string.IsNullOrWhiteSpace(name) ? Path : name;
        }
    }

    /// <summary>该目录下的视频（不递归）。</summary>
    public IReadOnlyList<MediaItem> Items { get; } = items;

    /// <summary>视频数量。</summary>
    public int Count => Items.Count;

    /// <summary>总时长。</summary>
    public TimeSpan TotalDuration => Items.Aggregate(TimeSpan.Zero, static (sum, item) => sum + item.Duration);
}

/// <summary>
/// 一次资料库扫描完成后的不可变视图。页面 ViewModel 只读取该快照，
/// 不直接接触扫描线程或文件系统。
/// </summary>
public sealed class LibrarySnapshot(
    IReadOnlyList<MediaItem> items,
    IReadOnlyList<MediaItem> musicItems,
    IReadOnlyList<MediaItem> videoItems,
    IReadOnlyList<AlbumGroup> albums,
    IReadOnlyList<ArtistGroup> artists,
    IReadOnlyList<VideoFolderGroup> videoFolders,
    IReadOnlyList<VideoDirectoryNode> videoRoots)
{
    /// <summary>空资料库快照。</summary>
    public static LibrarySnapshot Empty { get; } = new([], [], [], [], [], [], []);

    /// <summary>全部媒体条目。</summary>
    public IReadOnlyList<MediaItem> Items { get; } = items;

    /// <summary>音频条目，按标题排序。</summary>
    public IReadOnlyList<MediaItem> MusicItems { get; } = musicItems;

    /// <summary>视频条目，按标题排序。</summary>
    public IReadOnlyList<MediaItem> VideoItems { get; } = videoItems;

    /// <summary>专辑聚合。</summary>
    public IReadOnlyList<AlbumGroup> Albums { get; } = albums;

    /// <summary>艺术家聚合。</summary>
    public IReadOnlyList<ArtistGroup> Artists { get; } = artists;

    /// <summary>视频文件夹聚合。</summary>
    public IReadOnlyList<VideoFolderGroup> VideoFolders { get; } = videoFolders;

    /// <summary>视频资料库目录树根节点，保留空目录。</summary>
    public IReadOnlyList<VideoDirectoryNode> VideoRoots { get; } = videoRoots;
}

/// <summary>扫描进度快照。</summary>
public sealed record LibraryScanProgress(int Completed, int Total, string CurrentFile)
{
    /// <summary>0..1 的完成比例；总数未知时为 0。</summary>
    public double Fraction => Total <= 0 ? 0 : Math.Clamp((double)Completed / Total, 0, 1);
}
