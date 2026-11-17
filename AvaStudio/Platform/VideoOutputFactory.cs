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
    /// <param name="useNativeVideoRendering">
    /// Windows 下为 true 时使用原生子窗口 + libmpv <c>wid</c>，零拷贝、低 CPU/内存；
    /// 播放页标题栏变为实体，Avalonia 控件不能覆盖视频。
    /// </param>
    public static IVideoOutput Create(
        bool useGpuVideoRendering = false,
        bool useNativeVideoRendering = false)
    {
#if WINDOWS
        // 原生零拷贝优先；否则软件 render API；GPU 模式用于 HDR/高画质。
        if (useNativeVideoRendering)
        {
            return new WindowsNativeVideoOutput();
        }

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
