using System;
using System.Collections.Generic;
using System.IO;
using AvaStudio.Models;

namespace AvaStudio.Services.Library;

/// <summary>
/// 支持的媒体扩展名与类型判定。集中放置可避免扫描、拖放和文件选择器
/// 各自维护一份不一致的列表。
/// </summary>
public static class MediaFileTypes
{
    /// <summary>音频扩展名。</summary>
    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".flac", ".wav", ".m4a", ".aac", ".ogg", ".opus", ".wma",
    };

    /// <summary>视频扩展名。</summary>
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mkv", ".mov", ".m4v", ".avi", ".webm", ".wmv", ".flv", ".ts", ".m2ts",
    };

    /// <summary>文件选择器可用的媒体扩展名模式。</summary>
    public static IReadOnlyList<string> PickerPatterns { get; } =
    [
        "*.mp3", "*.flac", "*.wav", "*.m4a", "*.aac", "*.ogg", "*.opus", "*.wma",
        "*.mp4", "*.mkv", "*.mov", "*.m4v", "*.avi", "*.webm", "*.wmv", "*.flv", "*.ts", "*.m2ts",
    ];

    /// <summary>判断路径是否为受支持的媒体文件。</summary>
    public static bool IsSupported(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var extension = Path.GetExtension(path);
        return AudioExtensions.Contains(extension) || VideoExtensions.Contains(extension);
    }

    /// <summary>尝试根据扩展名判定媒体类型。</summary>
    public static bool TryClassify(string path, out MediaItemKind kind)
    {
        var extension = Path.GetExtension(path);
        if (AudioExtensions.Contains(extension))
        {
            kind = MediaItemKind.Audio;
            return true;
        }

        if (VideoExtensions.Contains(extension))
        {
            kind = MediaItemKind.Video;
            return true;
        }

        kind = MediaItemKind.Audio;
        return false;
    }
}
