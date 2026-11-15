using System.Collections.Concurrent;
using AvaStudio.Models;

namespace AvaStudio.Services.Library;

/// <summary>
/// 使用 TagLibSharp 读取音频/视频标签与内嵌封面，并生成 <see cref="MediaItem"/>。
/// 视频没有内嵌封面时会尝试从文件画面提取缩略图；音频则回退到同目录封面文件。
/// 该类只做单文件解析，不负责枚举目录或持久化；调用方可在后台线程并发使用。
/// </summary>
public sealed class MediaMetadataReader
{
    /// <summary>缺少艺术家标签时的回退文本。</summary>
    private const string UnknownArtist = "未知艺术家";

    /// <summary>缺少专辑标签时的回退文本。</summary>
    private const string UnknownAlbum = "未知专辑";

    /// <summary>视频缩略图的目标宽度（16:9，与卡片封面尺寸一致）。</summary>
    private const int VideoThumbnailWidth = 320;

    /// <summary>视频缩略图的目标高度。</summary>
    private const int VideoThumbnailHeight = 180;

    /// <summary>视频缩略图的 MIME 类型，与 <see cref="IVideoThumbnailProvider"/> 的 PNG 输出一致。</summary>
    private const string VideoThumbnailMimeType = "image/png";

    /// <summary>封面提取器。</summary>
    private readonly ArtworkStore _artworkStore;

    /// <summary>视频缩略图提取器；平台不支持时为 null。</summary>
    private readonly IVideoThumbnailProvider? _videoThumbnailProvider;

    /// <summary>目录 → 回退封面路径的缓存，避免同目录每首歌都探测文件。</summary>
    private readonly ConcurrentDictionary<string, string?> _folderArtworkCache =
        new(StringComparer.OrdinalIgnoreCase);

    public MediaMetadataReader(ArtworkStore artworkStore, IVideoThumbnailProvider? videoThumbnailProvider = null)
    {
        _artworkStore = artworkStore;
        _videoThumbnailProvider = videoThumbnailProvider;
    }

    /// <summary>
    /// 读取一个媒体文件的元数据。无法解析标签时会回退到文件名与空字段，
    /// 但仍返回可播放的条目；只有文件本身不存在时才应视为失败。
    /// </summary>
    public MediaItem Read(string path, MediaItem? previous = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var fileInfo = new FileInfo(path);
        if (!fileInfo.Exists)
        {
            throw new FileNotFoundException("媒体文件不存在。", path);
        }

        var kind = MediaFileTypes.TryClassify(path, out var classified)
            ? classified
            : MediaItemKind.Audio;

        var addedUtc = previous?.AddedUtc ?? DateTimeOffset.UtcNow;
        var lastPlayedUtc = previous?.LastPlayedUtc;

        string title = string.Empty;
        string artist = string.Empty;
        string album = string.Empty;
        string albumArtist = string.Empty;
        string genre = string.Empty;
        uint? trackNumber = null;
        uint? year = null;
        TimeSpan duration = TimeSpan.Zero;
        string? artworkPath = previous?.ArtworkPath;

        try
        {
            using var tagFile = TagLib.File.Create(path);
            var tag = tagFile.Tag;

            title = Normalize(tag.Title) ?? string.Empty;
            artist = Normalize(tag.FirstPerformer) ?? Normalize(tag.Performers.FirstOrDefault()) ?? string.Empty;
            album = Normalize(tag.Album) ?? string.Empty;
            genre = Normalize(tag.FirstGenre) ?? string.Empty;
            albumArtist = Normalize(tag.FirstAlbumArtist)
                ?? Normalize(tag.AlbumArtists.FirstOrDefault())
                ?? string.Empty;
            trackNumber = tag.Track == 0 ? null : tag.Track;
            year = tag.Year == 0 ? null : tag.Year;
            duration = tagFile.Properties?.Duration ?? TimeSpan.Zero;

            var picture = tag.Pictures
                .Where(static item => item.Data is { Count: > 0 })
                .OrderByDescending(static item => item.Type == TagLib.PictureType.FrontCover)
                .FirstOrDefault();
            if (picture is not null)
            {
                artworkPath = _artworkStore.Save(picture.Data.Data, picture.MimeType);
            }
        }
        catch (Exception)
        {
            // 标签损坏、格式不受支持或第三方解析器异常时保留文件级信息，
            // 保证一个坏文件不会中断整个资料库扫描。
        }

        // 视频不使用目录封面：优先保留内嵌封面，否则从视频画面提取缩略图，
        // 避免视频卡片显示与内容无关的 folder 封面。音频继续使用目录封面回退。
        if (kind == MediaItemKind.Video)
        {
            // 旧版本会把目录封面写到视频条目上；这类路径需要丢弃以换成真实画面缩略图。
            if (artworkPath is not null && IsFolderArtworkPath(path, artworkPath))
            {
                artworkPath = null;
            }

            artworkPath ??= TryExtractVideoThumbnail(path);
        }
        else
        {
            artworkPath ??= FindFolderArtwork(path);
        }

        var displayTitle = title;
        if (string.IsNullOrWhiteSpace(displayTitle))
        {
            displayTitle = System.IO.Path.GetFileNameWithoutExtension(path);
        }

        return new MediaItem
        {
            Path = path,
            Kind = kind,
            Title = string.IsNullOrWhiteSpace(displayTitle) ? path : displayTitle,
            Artist = string.IsNullOrWhiteSpace(artist) ? kind == MediaItemKind.Audio ? UnknownArtist : string.Empty : artist,
            Album = string.IsNullOrWhiteSpace(album) ? kind == MediaItemKind.Audio ? UnknownAlbum : string.Empty : album,
            Genre = genre,
            AlbumArtist = albumArtist,
            TrackNumber = trackNumber,
            Year = year,
            Duration = duration < TimeSpan.Zero ? TimeSpan.Zero : duration,
            ArtworkPath = artworkPath,
            FileSize = fileInfo.Exists ? fileInfo.Length : 0,
            FileModifiedTicks = fileInfo.Exists ? fileInfo.LastWriteTimeUtc.Ticks : 0,
            AddedUtc = addedUtc,
            LastPlayedUtc = lastPlayedUtc,
        };
    }

    /// <summary>
    /// 从视频画面提取缩略图并交给 <see cref="ArtworkStore"/> 缓存。
    /// 平台没有实现、提取失败或保存失败时返回 null；缩略图属于可选内容，不向外抛异常。
    /// </summary>
    private string? TryExtractVideoThumbnail(string path)
    {
        if (_videoThumbnailProvider is null)
        {
            return null;
        }

        try
        {
            var data = _videoThumbnailProvider.GetThumbnail(path, VideoThumbnailWidth, VideoThumbnailHeight);
            return _artworkStore.Save(data, VideoThumbnailMimeType);
        }
        catch (Exception)
        {
            // 系统缩略图处理器由第三方编解码器实现，异常类型不可预知；
            // 这里统一兜底，保证单个视频不会中断整个资料库扫描。
            return null;
        }
    }

    /// <summary>
    /// 判断封面路径是否来自媒体文件所在目录的封面文件。
    /// 内嵌封面与视频缩略图保存在 <see cref="ArtworkStore"/> 的独立目录中，
    /// 因此只有与媒体同目录的封面才可能是旧版视频的目录封面回退。
    /// </summary>
    private static bool IsFolderArtworkPath(string mediaPath, string artworkPath)
    {
        try
        {
            var mediaDirectory = System.IO.Path.GetDirectoryName(mediaPath);
            var artworkDirectory = System.IO.Path.GetDirectoryName(artworkPath);
            return !string.IsNullOrWhiteSpace(mediaDirectory)
                && string.Equals(mediaDirectory, artworkDirectory, StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>在媒体文件所在目录查找常见封面文件名。</summary>
    private string? FindFolderArtwork(string mediaPath)
    {
        try
        {
            var directory = System.IO.Path.GetDirectoryName(mediaPath);
            if (string.IsNullOrWhiteSpace(directory))
            {
                return null;
            }

            return _folderArtworkCache.GetOrAdd(directory, ProbeFolderArtwork);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // 目录不可访问时按无封面处理。
            return null;
        }
    }

    /// <summary>探测目录中的常见封面文件。</summary>
    private static string? ProbeFolderArtwork(string directory)
    {
        ReadOnlySpan<string> candidates =
        [
            "cover.jpg", "cover.png", "folder.jpg", "folder.png",
            "front.jpg", "front.png", "album.jpg", "album.png",
        ];

        foreach (var candidate in candidates)
        {
            var path = System.IO.Path.Combine(directory, candidate);
            if (File.Exists(path))
            {
                return path;
            }
        }

        return null;
    }

    /// <summary>去除标签首尾空白；空字符串转为 null 以便使用回退值。</summary>
    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
