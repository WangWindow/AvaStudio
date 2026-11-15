namespace AvaStudio.Platform.Unsupported;

/// <summary>Explicit degradation when the current platform has no app-private libmpv layout.</summary>
public sealed class UnsupportedNativeLibraryLocator : INativeLibraryLocator
{
    public string FileName => "libmpv";

    public string ResolveLibraryPath() =>
        throw new PlatformNotSupportedException("The current platform has no libmpv native library location.");
}
