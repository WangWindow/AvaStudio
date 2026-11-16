using AvaStudio.Platform.Unsupported;
#if LINUX
using AvaStudio.Platform.Linux;
#endif

namespace AvaStudio.Platform;

/// <summary>
/// 按平台选择视频输出。Windows 与 Linux 的软件回退都使用 render API 把画面画进
/// Avalonia 控件；Linux 默认使用 OpenGL 渲染以获得硬件加速。
/// </summary>
public static class VideoOutputFactory
{
    public static IVideoOutput Create()
    {
#if WINDOWS
        // Windows 统一使用软件 render API：视频成为普通 Avalonia 内容，
        // 不再创建原生子窗口，点击/双击/全屏/跨屏布局都由 Avalonia 处理。
        return new SoftwareVideoOutput();
#elif LINUX
        var software = string.Equals(
            Environment.GetEnvironmentVariable("AVASTUDIO_LINUX_VIDEO"),
            "software",
            StringComparison.OrdinalIgnoreCase);
        return software ? new SoftwareVideoOutput() : new LinuxOpenGlVideoOutput();
#else
        return new UnsupportedVideoOutput();
#endif
    }
}
