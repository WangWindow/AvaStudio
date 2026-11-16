using AvaStudio.Platform.Unsupported;
using AvaStudio.Services.Library;
#if WINDOWS
using AvaStudio.Platform.Windows;
#elif LINUX
using AvaStudio.Platform.Linux;
#endif

namespace AvaStudio.Platform;

/// <summary>
/// 视频缩略图提取器的编译期平台选择：Windows 使用系统 Shell 缩略图，
/// Linux 使用 libmpv 取帧；其余平台退化为不返回缩略图，保证资料库扫描在任何平台都能完成。
/// </summary>
public static class VideoThumbnailProviderFactory
{
    /// <summary>创建当前平台可用的缩略图提取器；始终返回非 null。</summary>
    public static IVideoThumbnailProvider Create()
    {
#if WINDOWS
        return new WindowsVideoThumbnailProvider();
#elif LINUX
        return new LinuxVideoThumbnailProvider();
#else
        return new UnsupportedVideoThumbnailProvider();
#endif
    }
}
