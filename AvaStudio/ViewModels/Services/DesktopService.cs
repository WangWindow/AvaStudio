using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using AvaStudio.Services.Library;

namespace AvaStudio.ViewModels.Services;

/// <summary>
/// UI 层的平台存储与系统外壳包装：文件选择器、文件夹选择器、打开目录/URI。
/// 不负责媒体扫描、播放或持久化；文件类型判定统一委托给 <see cref="MediaFileTypes"/>。
/// </summary>
public sealed class DesktopService
{
    /// <summary>媒体文件选择器标题。</summary>
    private const string MediaPickerTitle = "打开媒体文件";

    private static readonly FilePickerFileType MediaFileType = new("媒体文件")
    {
        Patterns = [.. MediaFileTypes.PickerPatterns],
        MimeTypes = ["video/*", "audio/*"],
    };

    private TopLevel? _topLevel;

    /// <summary>绑定当前窗口的 TopLevel；窗口关闭时传 null。</summary>
    public void Attach(TopLevel? topLevel)
    {
        _topLevel = topLevel;
    }

    /// <summary>打开多选文件选择器，返回真实本地路径。</summary>
    public async Task<IReadOnlyList<string>> PickMediaFilesAsync()
    {
        if (_topLevel?.StorageProvider is not { CanOpen: true } storageProvider)
        {
            return [];
        }

        try
        {
            var files = await storageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = MediaPickerTitle,
                AllowMultiple = true,
                FileTypeFilter = [MediaFileType],
            });

            return files
                .Select(file => file.TryGetLocalPath())
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Cast<string>()
                .ToArray();
        }
        catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException)
        {
            return [];
        }
    }

    /// <summary>打开单文件夹选择器。</summary>
    public async Task<string?> PickFolderAsync(string title)
    {
        if (_topLevel?.StorageProvider is not { CanPickFolder: true } storageProvider)
        {
            return null;
        }

        try
        {
            var folders = await storageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = title,
                AllowMultiple = false,
            });

            return folders
                .Select(folder => folder.TryGetLocalPath())
                .FirstOrDefault(path => !string.IsNullOrWhiteSpace(path));
        }
        catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>使用系统默认处理程序打开 URI。</summary>
    public bool OpenUri(string uri)
    {
        try
        {
            return Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true }) is not null;
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    /// <summary>使用系统文件管理器打开目录。</summary>
    public bool OpenDirectory(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return false;
        }

        try
        {
            return Process.Start(new ProcessStartInfo(directory) { UseShellExecute = true }) is not null;
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    /// <summary>展开拖放/选择得到的路径：文件夹递归展开，文件按扩展名过滤。</summary>
    public static IReadOnlyList<string> ExpandMediaPaths(IEnumerable<string> paths)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            if (Directory.Exists(path))
            {
                foreach (var file in EnumerateMediaFiles(path))
                {
                    if (seen.Add(file))
                    {
                        result.Add(file);
                    }
                }

                continue;
            }

            if (File.Exists(path) && MediaFileTypes.IsSupported(path))
            {
                var fullPath = Path.GetFullPath(path);
                if (seen.Add(fullPath))
                {
                    result.Add(fullPath);
                }
            }
        }

        return result;
    }

    /// <summary>
    /// 递归枚举目录中的媒体文件，跳过隐藏/系统/重解析点，避免符号链接环。
    /// 单个目录不可访问时跳过，不影响其它目录。
    /// </summary>
    public static IReadOnlyList<string> EnumerateMediaFiles(string directory)
    {
        const FileAttributes skipped =
            FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint;

        var results = new List<string>();
        var pending = new Stack<string>();
        pending.Push(directory);

        while (pending.Count > 0)
        {
            var current = pending.Pop();
            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(current);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var file in files)
            {
                if (!MediaFileTypes.IsSupported(file))
                {
                    continue;
                }

                try
                {
                    if ((File.GetAttributes(file) & skipped) != 0)
                    {
                        continue;
                    }

                    results.Add(Path.GetFullPath(file));
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    // 单文件不可访问时跳过。
                }
            }

            IEnumerable<string> directories;
            try
            {
                directories = Directory.EnumerateDirectories(current);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var child in directories)
            {
                try
                {
                    if ((File.GetAttributes(child) & skipped) == 0)
                    {
                        pending.Push(child);
                    }
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    // 无法读取属性的目录直接跳过。
                }
            }
        }

        results.Sort(StringComparer.OrdinalIgnoreCase);
        return results;
    }

    /// <summary>判断路径是否为受支持的媒体文件。</summary>
    public static bool IsSupportedMediaPath(string path) => MediaFileTypes.IsSupported(path);
}
