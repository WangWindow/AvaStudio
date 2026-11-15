using AvaStudio.Platform.Unsupported;
#if WINDOWS
using AvaStudio.Platform.Windows;
#elif LINUX
using AvaStudio.Platform.Linux;
#endif

namespace AvaStudio.Platform;

/// <summary>
/// Compile-time platform selection for the native video surface. The returned
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
        return new LinuxVideoOutput();
#else
        return new UnsupportedVideoOutput();
#endif
    }
}
