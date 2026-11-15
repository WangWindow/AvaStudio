#if WINDOWS
using System;
using System.IO;
using System.Runtime.InteropServices;

namespace AvaStudio.Platform.Windows;

/// <summary>
/// Windows layout: the library lives at <c>libmpv/&lt;win-rid&gt;/libmpv-2.dll</c> below the
/// app directory. System library paths are never searched.
/// </summary>
public sealed class WindowsNativeLibraryLocator : INativeLibraryLocator
{
    public const string LibraryFileName = "libmpv-2.dll";

    public string FileName => LibraryFileName;

    public string ResolveLibraryPath()
    {
        string rid = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "win-x64",
            Architecture.Arm64 => "win-arm64",
            _ => throw new PlatformNotSupportedException("libmpv is only supported on Windows x64 and arm64."),
        };

        string expectedPath = Path.Combine(AppContext.BaseDirectory, "libmpv", rid, LibraryFileName);
        if (!File.Exists(expectedPath))
        {
            throw new FileNotFoundException(
                $"无法加载 libmpv 播放引擎。请将原生库文件放到 {expectedPath}。",
                expectedPath);
        }

        return expectedPath;
    }
}
#endif
