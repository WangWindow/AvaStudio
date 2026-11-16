using AvaStudio.Platform.Unsupported;
#if WINDOWS
using AvaStudio.Platform.Windows;
#elif LINUX
using AvaStudio.Platform.Linux;
#endif

namespace AvaStudio.Platform;

/// <summary>
/// 按平台选择视频输出。Windows 与 Linux 的软件回退都使用 render API 把画面画进
/// Avalonia 控件；Linux 默认使用 OpenGL 渲染以获得硬件加速。
/// </summary>
public static class VideoOutputFactory
{
    /// <param name="useGpuVideoRendering">
    /// Windows 下为 true 时使用 OpenGL/ANGLE：支持 HDR 色调映射和 GPU 缩放，
    /// 但 4K 视频私有内存明显更高。Linux 仍按原有环境变量选择 OpenGL。
    /// </param>
    public static IVideoOutput Create(bool useGpuVideoRendering = false)
    {
#if WINDOWS
        // 默认软件 render API：视频是普通 Avalonia 内容，不创建原生子窗口。
        // GPU 模式由设置页显式开启（HDR/高画质），避免默认占用过高内存。
        return useGpuVideoRendering ? new WindowsOpenGlVideoOutput() : new SoftwareVideoOutput();
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
