using System.IO;
namespace AvaStudio.Platform;

/// <summary>
/// Resolves the app-private libmpv native library. Implementations only look below
/// <c>AppContext.BaseDirectory/libmpv/&lt;rid&gt;/</c>; there is no system-library fallback.
/// </summary>
public interface INativeLibraryLocator
{
    string FileName { get; }

    /// <summary>
    /// Returns the full path of the native library. Throws <see cref="FileNotFoundException"/>
    /// with the exact expected path when the library is missing.
    /// </summary>
    string ResolveLibraryPath();
}
