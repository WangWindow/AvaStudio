using System;
using System.IO;
using System.Security.Cryptography;

namespace AvaStudio.Services.Library;

/// <summary>
/// 管理从媒体标签中提取出来的封面图片。图片按内容哈希命名，
/// 相同封面只落盘一次；路径指向 <c>%LocalAppData%/AvaStudio/artwork</c>。
/// </summary>
public sealed class ArtworkStore
{
    /// <summary>封面缓存目录名。</summary>
    private const string ArtworkFolderName = "artwork";

    /// <summary>内容哈希截断长度；32 位十六进制足以避免本地冲突。</summary>
    private const int HashHexLength = 32;

    private readonly string _directory;

    public ArtworkStore(string settingsDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(settingsDirectory);
        _directory = Path.Combine(settingsDirectory, ArtworkFolderName);
    }

    /// <summary>封面缓存目录。</summary>
    public string DirectoryPath => _directory;

    /// <summary>
    /// 保存封面字节，返回可长期使用的本地路径；失败或没有内容时返回 null。
    /// 该方法可安全地在后台线程调用。
    /// </summary>
    public string? Save(byte[]? data, string? mimeType)
    {
        if (data is null || data.Length == 0)
        {
            return null;
        }

        try
        {
            var extension = GetExtension(mimeType);
            var hash = Convert.ToHexString(SHA256.HashData(data))[..HashHexLength].ToLowerInvariant();
            var path = Path.Combine(_directory, hash + extension);
            if (File.Exists(path))
            {
                return path;
            }

            System.IO.Directory.CreateDirectory(_directory);
            var temporaryPath = path + ".tmp";
            File.WriteAllBytes(temporaryPath, data);
            File.Move(temporaryPath, path, overwrite: true);
            return path;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // 封面是可选内容：读取失败不应导致整个扫描失败。
            return null;
        }
    }

    /// <summary>根据 MIME 类型选择图片扩展名。</summary>
    private static string GetExtension(string? mimeType)
    {
        if (string.IsNullOrWhiteSpace(mimeType))
        {
            return ".jpg";
        }

        var normalized = mimeType.ToLowerInvariant();
        if (normalized.Contains("png", StringComparison.Ordinal))
        {
            return ".png";
        }

        if (normalized.Contains("webp", StringComparison.Ordinal))
        {
            return ".webp";
        }

        if (normalized.Contains("bmp", StringComparison.Ordinal))
        {
            return ".bmp";
        }

        if (normalized.Contains("gif", StringComparison.Ordinal))
        {
            return ".gif";
        }

        return ".jpg";
    }
}
