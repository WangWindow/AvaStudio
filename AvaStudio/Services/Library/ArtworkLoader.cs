using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;

namespace AvaStudio.Services.Library;

/// <summary>
/// 封面图片加载器。按“路径 + 目标宽度”缓存解码结果，避免同一专辑封面
/// 在多张卡片中重复解码；解码在后台线程完成，调用方在 UI 线程设置属性。
/// </summary>
public sealed class ArtworkLoader
{
    /// <summary>默认卡片封面宽度。</summary>
    public const int CardWidth = 320;

    /// <summary>播放器封面宽度。</summary>
    public const int PlayerWidth = 192;

    private readonly ConcurrentDictionary<string, Bitmap?> _cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>异步加载封面；路径为空、文件不存在或解码失败时返回 null。</summary>
    public Task<Bitmap?> LoadAsync(string? path, int decodeWidth)
    {
        if (string.IsNullOrWhiteSpace(path) || decodeWidth <= 0)
        {
            return Task.FromResult<Bitmap?>(null);
        }

        var key = $"{path}|{decodeWidth}";
        if (_cache.TryGetValue(key, out var cached))
        {
            return Task.FromResult(cached);
        }

        return Task.Run(
            () =>
            {
                var bitmap = Decode(path, decodeWidth);
                _cache[key] = bitmap;
                return bitmap;
            });
    }

    /// <summary>同步解码，仅允许在后台线程调用。</summary>
    private static Bitmap? Decode(string path, int decodeWidth)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            using var stream = File.OpenRead(path);
            return Bitmap.DecodeToWidth(stream, decodeWidth);
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or NotSupportedException
                or ArgumentException
                or InvalidOperationException)
        {
            return null;
        }
    }
}
