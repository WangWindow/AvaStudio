using AvaStudio.Platform.Unsupported;
#if WINDOWS
using AvaStudio.Platform.Windows;
#elif LINUX
using AvaStudio.Platform.Linux;
#endif

namespace AvaStudio.Platform;

/// <summary>Compile-time platform selection for the app-private native library location.</summary>
public static class NativeLibraryLocator
{
    public static INativeLibraryLocator CreateDefault()
    {
#if WINDOWS
        return new WindowsNativeLibraryLocator();
#elif LINUX
        return new LinuxNativeLibraryLocator();
#else
        return new UnsupportedNativeLibraryLocator();
#endif
    }
}
