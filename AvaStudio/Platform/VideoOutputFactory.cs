using AvaStudio.Platform.Unsupported;
#if WINDOWS
using AvaStudio.Platform.Windows;
#elif LINUX
using AvaStudio.Platform.Linux;
#endif

namespace AvaStudio.Platform;

/// <summary>
/// Compile-time platform selection for the video surface. The returned
/// <see cref="IVideoOutput.Surface"/> is placed in the shell's visual tree and realized before
/// the playback runtime is created.
/// </summary>
public static class VideoOutputFactory
{
    public static IVideoOutput Create()
    {
#if WINDOWS
        return new WindowsVideoOutput();
#elif LINUX
        // 默认走 OpenGL 渲染；软件渲染保留为无 GL 集成环境的回退路径。
        var software = string.Equals(
            Environment.GetEnvironmentVariable("AVASTUDIO_LINUX_VIDEO"),
            "software",
            StringComparison.OrdinalIgnoreCase);
        return software ? new LinuxSoftwareVideoOutput() : new LinuxOpenGlVideoOutput();
#else
        return new UnsupportedVideoOutput();
#endif
    }
}
